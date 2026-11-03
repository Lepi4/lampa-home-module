import android.content.*;
import android.media.*;
import android.os.Looper;
import java.lang.reflect.*;
import org.json.JSONObject;
import android.test.mock.MockContentResolver;

/** Runs against the installed APK's real Media3/factory classes, with a fake HDMI announcement. */
public class AudioRecoveryProbe {
 public static void main(String[] args)throws Exception{
  Looper.prepareMainLooper();
  Object thread=Class.forName("android.app.ActivityThread").getMethod("systemMain").invoke(null);
  Context system=(Context)thread.getClass().getMethod("getSystemContext").invoke(thread);
  final MockContentResolver resolver=new MockContentResolver();
  ContentProvider settingsProvider=new ContentProvider(){
   public boolean onCreate(){return true;}
   public android.os.Bundle call(String m,String a,android.os.Bundle e){android.os.Bundle b=new android.os.Bundle();b.putString("value","0");return b;}
   public android.database.Cursor query(android.net.Uri u,String[] p,String s,String[] a,String o){android.database.MatrixCursor c=new android.database.MatrixCursor(new String[]{"name","value"});c.addRow(new Object[]{"setting","0"});return c;}
   public String getType(android.net.Uri u){return null;}
   public android.net.Uri insert(android.net.Uri u,ContentValues v){return null;}
   public int delete(android.net.Uri u,String s,String[] a){return 0;}
   public int update(android.net.Uri u,ContentValues v,String s,String[] a){return 0;}
  };
  android.content.pm.ProviderInfo providerInfo=new android.content.pm.ProviderInfo();providerInfo.authority="settings";
  settingsProvider.attachInfo(system,providerInfo);resolver.addProvider("settings",settingsProvider);
  Context context=new ContextWrapper(system){
   public Context getApplicationContext(){return this;}
   public ContentResolver getContentResolver(){return resolver;}
   public Object getSystemService(String name){return Context.UI_MODE_SERVICE.equals(name)?null:super.getSystemService(name);}
   public Intent registerReceiver(BroadcastReceiver receiver,IntentFilter filter){
    return new Intent(AudioManager.ACTION_HDMI_AUDIO_PLUG).putExtra(AudioManager.EXTRA_AUDIO_PLUG_STATE,1)
     .putExtra(AudioManager.EXTRA_ENCODINGS,new int[]{AudioFormat.ENCODING_PCM_16BIT,AudioFormat.ENCODING_AC3})
     .putExtra(AudioManager.EXTRA_MAX_CHANNEL_COUNT,6);
   }
  };
  Class<?> drc=Class.forName("top.rootu.lampa.player.DrcAudioProcessor"),factory=Class.forName("top.rootu.lampa.player.NoReuseRenderersFactory");
  Object f=factory.getConstructor(Context.class,drc).newInstance(context,drc.getConstructor().newInstance());
  Method enable=factory.getMethod("setAllowPassthrough",boolean.class);enable.invoke(f,true);
  Method build=factory.getDeclaredMethod("buildAudioSink",Context.class,boolean.class,boolean.class);build.setAccessible(true);build.invoke(f,context,false,false);
  Field active=factory.getDeclaredField("activeAudioSink");active.setAccessible(true);Object sink=active.get(f);
  Class<?> caps=Class.forName("androidx.media3.exoplayer.audio.AudioCapabilities");
  Field capabilities=sink.getClass().getDeclaredField("audioCapabilities");capabilities.setAccessible(true);
  Field looper=sink.getClass().getDeclaredField("playbackLooper");looper.setAccessible(true);looper.set(sink,Looper.myLooper());
  Object pcm=caps.getField("DEFAULT_AUDIO_CAPABILITIES").get(null);
  Method supports=caps.getMethod("supportsEncoding",int.class);
  capabilities.set(sink,pcm);if((Boolean)supports.invoke(capabilities.get(sink),5))throw new AssertionError("Fixture must start PCM-only");
  factory.getMethod("refreshAudioCapabilities",Context.class).invoke(f,context);
  if(!(Boolean)supports.invoke(capabilities.get(sink),5))throw new AssertionError("Recovered HDMI AC3 capabilities were not restored");
  enable.invoke(f,false);capabilities.set(sink,pcm);factory.getMethod("refreshAudioCapabilities",Context.class).invoke(f,context);
  if((Boolean)supports.invoke(capabilities.get(sink),5))throw new AssertionError("Disabled passthrough was re-enabled");
  System.out.println(new JSONObject().put("real_installed_classes",true).put("recover_pcm_to_ac3",true).put("disabled_remains_pcm",true).put("hardware_bitstream_test",false));System.exit(0);
 }
}
