using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using VideoToUnity.Editor;

namespace VideoToUnity.Tests
{
    public sealed class BackgroundEditorTests
    {
        private string folder, source;
        private Type operation;
        private GameObject root;
        private EditorWindow window;
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        [SetUp] public void SetUp()
        {
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), ".video-to-unity-validation")))
                Assert.Ignore("Run only in an isolated, marked native verification project.");
            folder="Assets/BackgroundNative_"+Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets",Path.GetFileName(folder));
            string package=UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VideoEffectAsset).Assembly).resolvedPath;
            source=Path.Combine(package,"Samples~/BackgroundRemoval/flat-black.mp4");
            Assert.IsTrue(File.Exists(source));
            operation=typeof(BackgroundMatte).Assembly.GetType("VideoToUnity.Editor.BackgroundRemovalOperation",true);
        }

        [UnityTearDown] public IEnumerator TearDown()
        {
            if (operation!=null) { Call("Cancel"); yield return Wait(()=>!(bool)Property("Busy")); }
            if(window!=null) UnityEngine.Object.DestroyImmediate(window);
            if(root!=null) UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.DeleteAsset(folder);
        }

        [Test] public void WindowCreatesItsNativeToolkitTreeWithoutOpeningAWindow()
        {
            Type type=typeof(BackgroundMatte).Assembly.GetType("VideoToUnity.Editor.BackgroundRemovalWindow",true);
            window=(EditorWindow)ScriptableObject.CreateInstance(type);
            type.GetMethod("CreateGUI",BindingFlags.Public|BindingFlags.Instance).Invoke(window,null);
            Assert.NotNull(window.rootVisualElement.Q("original-stage"));
            Assert.NotNull(window.rootVisualElement.Q("result-stage"));
            Assert.That(window.rootVisualElement.Query<Button>().ToList().Count,Is.GreaterThanOrEqualTo(5));
            Assert.That(window.rootVisualElement.Query<TextField>().ToList().Count,Is.GreaterThanOrEqualTo(3));
        }

        [UnityTest] public IEnumerator PackedUiExportImportsAndRendersProtectedBlack()
        { yield return ExportAndRender(VideoConversionFormat.PackedH264); }

        [UnityTest] public IEnumerator NativeAlphaUiExportImportsAndRendersProtectedBlack()
        { yield return ExportAndRender(VideoConversionFormat.AlphaWebM); }

        private IEnumerator ExportAndRender(VideoConversionFormat format)
        {
            string extension=format==VideoConversionFormat.PackedH264?".mp4":".webm";
            var options=new BackgroundOptions { BorderConnected=false,
                ProtectedRegions=new[]{new BackgroundKeepRegion(.3125f,.28125f,.375f,.4375f)} };
            Call("StartConvert",source,"ffmpeg","ffprobe",options,format,folder+"/effect"+extension,folder+"/effect.asset");
            yield return Wait(()=>!(bool)Property("Busy"));
            Assert.IsFalse((bool)Property("Failed"),(string)Property("Message"));
            var effect=AssetDatabase.LoadAssetAtPath<VideoEffectAsset>(folder+"/effect.asset");
            Assert.NotNull(effect); Assert.NotNull(effect.clip); Assert.IsFalse(effect.premultiplied);
            Assert.AreEqual(format==VideoConversionFormat.PackedH264?VideoAlphaMode.PackedAlpha:VideoAlphaMode.NativeAlpha,effect.alphaMode);
            Assert.AreEqual(new Vector2Int(160,96),effect.contentSize);
            BackgroundPreview reference;
            using(var previewJob=BackgroundVideoProcessor.Start(new BackgroundRequest{
                SourcePath=source,TemporaryDirectory=Path.GetFullPath(folder),Kind=BackgroundJobKind.Preview,
                Options=options,PreviewMaximumWidth=160}))
            {
                yield return Wait(()=>previewJob.Completion.IsCompleted);
                reference=previewJob.Completion.GetAwaiter().GetResult().Preview;
            }
            int softX=-1,softY=-1;
            for(int y=5;y<30 && softX<0;y++)for(int x=118;x<143;x++)
            {byte a=reference.ResultRgba[(y*160+x)*4+3];if(a>64 && a<192){softX=x;softY=y;break;}}
            Assert.That(softX,Is.GreaterThanOrEqualTo(0),"Fixture must contain a partial-alpha glow edge.");
            root=new GameObject("Background export playback"); var player=root.AddComponent<VideoEffectPlayer>();
            player.effect=effect; player.Play(); yield return Wait(()=>player.Texture!=null,()=>player.LastError);
            player.Pause();
            var material=new Material(Resources.Load<Shader>("VideoToUnity/VideoComposite"));
            var target=new RenderTexture(160,96,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            var read=new Texture2D(160,96,TextureFormat.RGBA32,false,true); var previous=RenderTexture.active;
            bool previousSrgb=GL.sRGBWrite;
            try
            {
                target.Create();RenderTexture.active=target;GL.sRGBWrite=false;GL.Clear(false,true,Color.blue);
                VideoEffectView.ApplyMaterial(material,effect,1,Vector2.one);Graphics.Blit(player.Texture,target,material);
                RenderTexture.active=target;read.ReadPixels(new Rect(0,0,160,96),0,0);read.Apply();
                Assert.That(read.GetPixel(5,48).b,Is.EqualTo(1).Within(.06),"Outer background must be transparent.");
                Assert.That(read.GetPixel(80,48).maxColorComponent,Is.LessThan(.06),"Protected black artwork must stay opaque.");
                Assert.That(read.GetPixel(51,48).r,Is.GreaterThan(.8),"Original bright frame must remain visible.");
                int offset=(softY*160+softX)*4;float alpha=reference.ResultRgba[offset+3]/255f;
                Color foreground=new Color(reference.ResultRgba[offset]/255f,reference.ResultRgba[offset+1]/255f,reference.ResultRgba[offset+2]/255f,1);
                if(QualitySettings.activeColorSpace==ColorSpace.Linear)foreground=foreground.linear;
                Color expected=foreground*alpha+Color.blue*(1-alpha);
                Color actual=read.GetPixel(softX,95-softY);
                Assert.That(actual.r,Is.EqualTo(expected.r).Within(.065),"Soft edge red");
                Assert.That(actual.g,Is.EqualTo(expected.g).Within(.065),"Soft edge green");
                Assert.That(actual.b,Is.EqualTo(expected.b).Within(.065),"Soft edge blue/alpha");
            }
            finally
            {
                RenderTexture.active=previous;GL.sRGBWrite=previousSrgb;target.Release();
                UnityEngine.Object.DestroyImmediate(material);UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(read);
            }
        }

        [Test] public void ExistingOutputIsRejectedBeforeAnyOverwrite()
        {
            string destination=folder+"/occupied.mp4";
            var expected=new byte[]{1,7,9,5};File.WriteAllBytes(destination,expected);
            Assert.Throws<TargetInvocationException>(()=>Call("StartConvert",source,"ffmpeg","ffprobe",new BackgroundOptions(),VideoConversionFormat.PackedH264,destination,folder+"/new.asset"));
            CollectionAssert.AreEqual(expected,File.ReadAllBytes(destination));
            Assert.IsFalse((bool)Property("Busy"));Assert.IsFalse(File.Exists(folder+"/new.asset"));
        }

        [UnityTest] public IEnumerator CancelledPreviewLeavesOperationIdle()
        {
            object previousOutput=Property("LastOutput");
            Call("StartPreview",source,"ffmpeg","ffprobe",new BackgroundOptions(),0.0);
            Call("Cancel");yield return Wait(()=>!(bool)Property("Busy"));
            Assert.IsFalse((bool)Property("Failed"),(string)Property("Message"));
            Assert.AreSame(previousOutput,Property("LastOutput"));
        }

        [UnityTest] public IEnumerator AnalyzeThenPreviewKeepsEnclosedBlackAndRemovesOuterBackground()
        {
            Type type=typeof(BackgroundMatte).Assembly.GetType("VideoToUnity.Editor.BackgroundRemovalWindow",true);
            const BindingFlags instance=BindingFlags.Instance|BindingFlags.NonPublic;
            window=(EditorWindow)ScriptableObject.CreateInstance(type);
            type.GetField("sourcePath",instance).SetValue(window,source);
            type.GetMethod("CreateGUI").Invoke(window,null);
            Call("StartAnalyze",source,"ffmpeg","ffprobe");
            yield return Wait(()=>!(bool)Property("Busy"));
            Assert.IsFalse((bool)Property("Failed"),(string)Property("Message"));
            Assert.IsTrue(((BackgroundAnalysis)Property("LastAnalysis")).Reliable);
            Assert.AreEqual(2.0,(double)type.GetField("durationSeconds",instance).GetValue(window),.01);
            Assert.NotNull(type.GetField("shownAnalysis",instance).GetValue(window),"Analysis must refresh before the job resets its kind.");
            Call("StartPreview",source,"ffmpeg","ffprobe",new BackgroundOptions(),.5);
            yield return Wait(()=>!(bool)Property("Busy"));
            Assert.IsFalse((bool)Property("Failed"),(string)Property("Message"));
            var preview=((BackgroundResult)Property("LastResult")).Preview;
            Assert.AreEqual(160,preview.Width);Assert.AreEqual(96,preview.Height);
            Assert.AreEqual(255,preview.OriginalRgba[(48*160+5)*4+3]);
            Assert.AreEqual(0,preview.ResultRgba[(48*160+5)*4+3]);
            Assert.AreEqual(255,preview.ResultRgba[(48*160+80)*4+3]);
            Assert.NotNull(type.GetField("previewResult",instance).GetValue(window));
            var input=(TextField)type.GetField("sourceField",instance).GetValue(window);
            input.value=source+".missing.mp4";
            // A window created without Show has no panel to dispatch TextField change events.
            type.GetMethod("ChangeSource",instance).Invoke(window,new object[]{input.value});
            Assert.IsNull(type.GetField("previewResult",instance).GetValue(window));
            Assert.AreEqual(0.0,(double)type.GetField("durationSeconds",instance).GetValue(window));
            Assert.AreEqual(DisplayStyle.None,window.rootVisualElement.Q("result-image").style.display.value);
        }

        private object Property(string name) => operation.GetProperty(name,Flags).GetValue(null);
        private void Call(string name,params object[] arguments) => operation.GetMethod(name,Flags).Invoke(null,arguments);
        private IEnumerator Wait(Func<bool> condition,Func<string> error=null)
        {
            double until=EditorApplication.timeSinceStartup+90;
            while(!condition() && EditorApplication.timeSinceStartup<until)
            { if(error!=null)Assert.IsNull(error());EditorApplication.QueuePlayerLoopUpdate();yield return null; }
            Assert.IsTrue(condition(),"Background native workflow timed out.");
        }
    }
}
