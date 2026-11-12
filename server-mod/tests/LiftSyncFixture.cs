using HomeMedia;
using Newtonsoft.Json.Linq;
class LiftSyncFixture {
static int passed;
static void Check(string name,bool condition){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
static TlV2.Result Send(LiftTarget target,string hash,long at,string ev,int position,JObject resume,string op="progress")=>HomeDb.With(c=>TlV2.ApplyBatch(c,new JArray(new JObject{
 ["id"]=target.Identity,["h"]=hash,["c"]=target.Card,["u"]=at,["event"]=ev,["t"]=position,["d"]=3600,["p"]=position/36.0,["op"]=op,["resume"]=resume
}),TlV2.InTx)).results[0];
static Profiles.Scope Owner(string device,string profile="u_default")=>new(profile,device,"fixture",null,true,false,18,1);
static void Main(){
 var fixture=Path.Combine(AppContext.BaseDirectory,"lift-sync-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);Directory.SetCurrentDirectory(fixture);
 Environment.SetEnvironmentVariable("HOME_PROFILES_ENABLED","1");HomeDb.With(c=>{});Profiles.Activate();
 var target=LiftTarget.Parse("tv",424242,1,2);long now=HomeDb.Now()-120000;string hash;JObject resume,binding;
 using(Profiles.Enter(Owner("device-a"))){
  binding=LiftBindings.Put(target,123,false,3,7);hash=LiftBindings.ProgressHash(target);
  resume=new JObject{["src"]="lift",["item"]="123",["season"]=1,["ep"]=2,["voice_name"]="Voice A",["codec"]="vp9",["edition"]=binding["edition"],["binding_revision"]=binding["revision"]};
  Check("Lift progress accepted from device A",Send(target,hash,now,"100:1",600,resume).Status=="accepted");
 }
 using(Profiles.Enter(Owner("device-b"))){
  var row=HomeDb.With(c=>TlV2.Resolve(c,target.Identity,hash,target.Card));var lp=LastPlayStore.GetForRead(target.Card);
  Check("device B receives 600 seconds from A",row?.T==600 && (long?)lp?["pos"]==600);
  Check("Lift source, voice, episode and edition survive write/read",(string)lp?["src"]=="lift"&&(string)lp?["voice_name"]=="Voice A"&&(int?)lp?["ep"]==2&&(string)lp?["edition"]==(string)binding["edition"]&&lp?["url"]==null);
  Check("same Lift edition is compatible",(bool)LiftService.PositionContext(target,(string)binding["revision"])["compatible"]);
  Check("second device uses same transport hash",LiftBindings.ProgressHash(target)==hash);
  Check("device B can advance progress",Send(target,hash,now+1000,"200:1",900,resume).Status=="accepted");
 }
 using(Profiles.Enter(Owner("device-a"))){
  Send(target,hash,now+500,"100:2",700,resume);
  Check("late A event cannot overwrite B position",HomeDb.With(c=>TlV2.Resolve(c,target.Identity,hash,target.Card)).T==900);
 }
 using(Profiles.Enter(Owner("device-c","other-profile")))Check("another profile cannot read Lift progress",HomeDb.With(c=>TlV2.Resolve(c,target.Identity,hash,target.Card))==null);
 using(Profiles.Enter(Owner("device-a"))){
  var torrent=new JObject{["src"]="url",["url"]="http://fixture/stream",["season"]=1,["ep"]=2};
  Check("torrent can update the same canonical episode",Send(target,"222",now+2000,"300:1",1200,torrent).Status=="accepted");
  Check("Lift receives progress from torrent source",HomeDb.With(c=>TlV2.Resolve(c,target.Identity,hash,target.Card)).T==1200);
  Check("different source is identified as another video edition",!(bool)LiftService.PositionContext(target,(string)binding["revision"])["compatible"]);
  Send(target,hash,now+3000,"400:1",0,null,"reset");
 }
 using(Profiles.Enter(Owner("device-b"))){
  var reset=HomeDb.With(c=>TlV2.Resolve(c,target.Identity,hash,target.Card));
  Check("reset reaches other device and removes Continue",reset.T==0&&reset.Op=="reset"&&LastPlayStore.GetForRead(target.Card)==null);
  var other=LiftTarget.Parse("tv",424242,1,3);
  Check("next episode does not inherit previous episode position",HomeDb.With(c=>TlV2.Resolve(c,other.Identity,LiftBindings.ProgressHash(other),other.Card))==null);
  Send(target,hash,now+4000,"500:1",3600,resume,"completed");
  Check("completed Lift event is persisted",(bool)LastPlayStore.GetForRead(target.Card)["done"]);
 }
 Console.WriteLine("TOTAL "+passed);
}
}
