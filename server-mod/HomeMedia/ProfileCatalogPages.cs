using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Public catalog pages are filled AFTER availability/age filtering. Personal
// collections are deliberately excluded: their small size may be legitimate.
public static class ProfileCatalogPages
{
    sealed class Pool
    {
        public readonly SemaphoreSlim Gate=new(1);
        public readonly List<JArray> Pages=new();
        public readonly Queue<JObject> Pending=new();
        public JArray Candidates=new();
        public readonly HashSet<string> Seen=new();
        public int Next=1,Total=1;
        public bool End;
        public DateTime LastUsed=DateTime.UtcNow;
        public readonly DateTime Created=DateTime.UtcNow;
    }
    static readonly ConcurrentDictionary<string,Pool> Pools=new();
    sealed class SourcePage
    {
        public readonly DateTime Created=DateTime.UtcNow;
        public Lazy<Task<(JArray results,int pages)>> Work;
    }
    static readonly object sourceLock=new();
    static readonly Dictionary<string,SourcePage> Sources=new();
    // Only unfiltered public source pages belong here. Each consumer receives a
    // clone; viewer policy and the immutable pagination pool remain downstream.
    public static async Task<(JArray results,int pages)> Source(string key,Func<Task<(JArray results,int pages)>> load)
    {
        SourcePage entry;
        lock(sourceLock){
            foreach(var old in Sources.Where(x=>DateTime.UtcNow-x.Value.Created>TimeSpan.FromMinutes(2)&&x.Value.Work.IsValueCreated&&x.Value.Work.Value.IsCompleted).Select(x=>x.Key).ToArray())Sources.Remove(old);
            if(!Sources.TryGetValue(key,out entry)){
                // Bounded retention; eviction never changes an open grid's pool.
                if(Sources.Count>=256){var oldest=Sources.Where(x=>x.Value.Work.IsValueCreated&&x.Value.Work.Value.IsCompleted).OrderBy(x=>x.Value.Created).FirstOrDefault();if(oldest.Key!=null)Sources.Remove(oldest.Key);}
                entry=new SourcePage{Work=new Lazy<Task<(JArray,int)>>(load)};
                if(Sources.Count<256)Sources[key]=entry;
            }
        }
        try{var value=await entry.Work.Value;return((JArray)value.results.DeepClone(),value.pages);}
        catch{lock(sourceLock){if(Sources.TryGetValue(key,out var current)&&ReferenceEquals(current,entry))Sources.Remove(key);}throw;}
    }
    public static bool Supports(string mode)=>new[]{"top_new","new_movie","new_serial","new_cartoon","new_cartoon_series","anime","ru_movie","ru_serial"}.Contains(mode);
    static string ModeOf(string key){ var p=(key??"").Split('|'); return p.Length>3?p[3]:""; }
    public static async Task<(JArray results,int pages,bool more)> Get(string key,int page,Func<int,Task<(JArray results,int pages)>> load,Func<JArray,Task> verify,CancellationToken cancellation=default,bool fill=false)
    {
        foreach(var pair in Pools.Where(x=>DateTime.UtcNow-x.Value.LastUsed>TimeSpan.FromHours(4)).ToArray())Pools.TryRemove(pair.Key,out _);
        var pool=Pools.GetOrAdd(key,_=>new Pool());
        // A new first-page request can refresh a stale snapshot; open grids use
        // their own catalog_session, so another viewer cannot replace their pool.
        if(page==1&&DateTime.UtcNow-pool.Created>TimeSpan.FromMinutes(10)){
            var next=new Pool();if(Pools.TryUpdate(key,next,pool))pool=next;else pool=Pools[key];
        }
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        await pool.Gate.WaitAsync(budget.Token);
        pool.LastUsed=DateTime.UtcNow;
        try
        {
            async Task PublishCandidates(){
                var verified=(JArray)pool.Candidates.DeepClone();
                await verify(verified).WaitAsync(budget.Token);
                foreach(var card in verified.Children<JObject>())pool.Pending.Enqueue(card);
                pool.Candidates=new JArray();
            }
            int scans=0;var deadline=DateTime.UtcNow.AddSeconds(fill?18:12);
            // Лента лаунчера ждёт полный ряд. Короткий показ через две секунды оставляем только сетке каталога.
            var displayDeadline=DateTime.UtcNow.AddSeconds(fill?18:2);
            while(pool.Pages.Count<page)
            {
                // A previous verification may have been cancelled after the last
                // source page. Preserve and verify those candidates on retry.
                if(pool.End&&pool.Candidates.Count>0){
                    await PublishCandidates();
                }
                while(pool.Pending.Count<20&&!pool.End&&scans<30&&DateTime.UtcNow<deadline)
                {
                    var candidates=pool.Candidates;
                    // Редкий детский отбор: за две секунды подходящей карточки может не попасться, и лента обрывается на последней найденной.
                    while(candidates.Count<20-pool.Pending.Count&&!pool.End&&scans<30&&DateTime.UtcNow<deadline)
                    {
                        var raw=await load(pool.Next).WaitAsync(budget.Token);scans++;
                        foreach(var card in raw.results.Children<JObject>())
                        {
                            string id=CardKind.Media(card)+":"+card["id"];
                            if(pool.Seen.Add(id))candidates.Add((JObject)card.DeepClone());
                        }
                        // Меньший total_pages на одном сбойном ответе не закрывает каталог: раньше «1 страница» после седьмой загрузки ставила конец всему списку.
                        if(raw.pages>pool.Total)pool.Total=raw.pages;
                        pool.Next++;
                        // End at the last valid source page; requesting 501 from TMDB
                        // yields an error, so waiting for an extra empty response never finishes.
                        pool.End=pool.Next>pool.Total;
                        // Child policy may already have reduced a source page to
                        // one card. Do not collect twenty such pages before the
                        // first availability check and the partial-page deadline.
                        if(!fill&&candidates.Count>0&&DateTime.UtcNow>=displayDeadline)break;
                    }
                    if(candidates.Count>0)await PublishCandidates();
                    // Sparse child catalogs need not hold already allowed cards
                    // while searching for a full twenty. Publish a short page;
                    // keep the cursor/pending candidates and truthful has_more.
                    if(!fill&&pool.Pending.Count>0&&DateTime.UtcNow>=displayDeadline)break;
                }
                var filled=new JArray();while(filled.Count<20&&pool.Pending.Count>0)filled.Add(pool.Pending.Dequeue());
                if(filled.Count==0&&!pool.End)
                {
                    // Пустую страницу не фиксируем: клиент может запросить её снова и сканирование продолжится.
                    DLogStore.Server("w","cat_page","empty_retry","page="+page+" scans="+scans+" mode="+ModeOf(key));
                    return(new JArray(),page+1,true);
                }
                if(filled.Count==0&&pool.End)break;
                pool.Pages.Add(filled);
                if(pool.End&&pool.Pending.Count==0)break;
                if(scans>=30||DateTime.UtcNow>=deadline)break;
            }
            var result=page<=pool.Pages.Count?(JArray)pool.Pages[page-1].DeepClone():new JArray();
            bool more=page<pool.Pages.Count||pool.Pending.Count>0||!pool.End;
            string why=pool.End?"source_end":scans>=30?"scan_cap":DateTime.UtcNow>=deadline?"deadline":DateTime.UtcNow>=displayDeadline?"partial_ready":"filled";
            string last="";
            if(result.Last is JObject tail) last=((string)tail["title"]??(string)tail["name"]??"");
            if(last.Length>40) last=last.Substring(0,40);
            DLogStore.Server(result.Count==0?"w":"i","cat_page",why,"page="+page+" n="+result.Count+" more="+(more?1:0)+" scans="+scans+" end="+(pool.End?1:0)+" pending="+pool.Pending.Count+" mode="+ModeOf(key)+" last="+last);
            return(result,more?Math.Max(page+1,pool.Pages.Count):Math.Max(1,pool.Pages.Count),more);
        }
        finally{pool.Gate.Release();}
    }
}
