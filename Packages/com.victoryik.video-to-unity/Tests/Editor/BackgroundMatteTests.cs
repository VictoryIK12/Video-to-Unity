using System;
using System.Threading;
using NUnit.Framework;
using VideoToUnity.Editor;

namespace VideoToUnity.Tests
{
    public sealed class BackgroundMatteTests
    {
        [Test] public void DetectsBorderBlackWithoutIncludingForeground()
        {
            var source = Solid(12,8,0,0,0);
            for(int y=2;y<6;y++) for(int x=2;x<10;x++) Pixel(source,12,x,y,230,40,20);
            var result = BackgroundMatte.Analyze(new[]{source},12,8);
            Assert.IsTrue(result.Reliable); Assert.AreEqual(1,result.Confidence);
            Assert.AreEqual(0,result.Red); Assert.AreEqual(0,result.Green); Assert.AreEqual(0,result.Blue);
        }

        [Test] public void DetectsColoredBackgroundWithCompressionNoise()
        {
            var a=Solid(12,8,3,181,31); var b=Solid(12,8,1,178,29);
            var result=BackgroundMatte.Analyze(new[]{a,b},12,8);
            Assert.IsTrue(result.Reliable); Assert.That(result.Green,Is.InRange(178,181));
            Assert.That(result.Red,Is.LessThan(5)); Assert.That(result.Blue,Is.InRange(29,31));
        }

        [Test] public void MixedBordersRequireExplicitManualChoice()
        {
            var frames=new[]{Solid(4,4,0,0,0),Solid(4,4,255,0,0),Solid(4,4,0,255,0),Solid(4,4,0,0,255)};
            var result=BackgroundMatte.Analyze(frames,4,4);
            Assert.IsFalse(result.Reliable); Assert.That(result.Confidence,Is.EqualTo(.25));
            Assert.Throws<ArgumentException>(()=>new BackgroundMatteSession(4,4,new BackgroundOptions(),result));
            var output=new byte[64]; new BackgroundMatteSession(4,4,Manual()).Process(frames[0],output);
            Assert.AreEqual(0,output[3]);
        }

        [Test] public void BorderConnectionPreservesEnclosedBlackArtwork()
        {
            var source=Solid(8,8,0,0,0);
            for(int y=1;y<7;y++) for(int x=1;x<7;x++) if(x==1||x==6||y==1||y==6) Pixel(source,8,x,y,255,255,255);
            var output=new byte[source.Length]; var options=Manual(); options.BorderConnected=true;
            new BackgroundMatteSession(8,8,options).Process(source,output);
            Assert.AreEqual(0,Alpha(output,8,0,0)); Assert.AreEqual(255,Alpha(output,8,3,3));
            Assert.AreEqual(0,output[(3*8+3)*4]); Assert.AreEqual(255,Alpha(output,8,1,1));
            options.BorderConnected=false; new BackgroundMatteSession(8,8,options).Process(source,output);
            Assert.AreEqual(0,Alpha(output,8,3,3));
        }

        [Test] public void ProtectedRectangleUsesTopLeftCoordinatesAndPreservesBlack()
        {
            var source=Solid(8,4,0,0,0); var options=Manual();
            options.ProtectedRegions=new[]{new BackgroundKeepRegion(.25f,0,.25f,.5f)};
            var output=new byte[source.Length]; new BackgroundMatteSession(8,4,options).Process(source,output);
            Assert.AreEqual(255,Alpha(output,8,2,0)); Assert.AreEqual(255,Alpha(output,8,3,1));
            Assert.AreEqual(0,Alpha(output,8,2,3)); Assert.AreEqual(0,Alpha(output,8,4,0));
            Assert.AreEqual(0,output[(1*8+3)*4]);
        }

        [Test] public void SessionSnapshotsOptionsAndDoesNotModifySource()
        {
            var options=Manual(); options.ProtectedRegions=new[]{new BackgroundKeepRegion(0,0,.5f,.5f)};
            var session=new BackgroundMatteSession(4,4,options); options.Red=255;
            options.ProtectedRegions[0]=new BackgroundKeepRegion(.5f,.5f,.5f,.5f);
            var source=Solid(4,4,0,0,0); var original=(byte[])source.Clone(); var output=new byte[source.Length];
            session.Process(source,output);
            Assert.AreEqual(255,Alpha(output,4,0,0)); Assert.AreEqual(0,Alpha(output,4,3,3));
            CollectionAssert.AreEqual(original,source);
        }

        [Test] public void SoftMatteReturnsStraightColorThatRecompositesOverBlack()
        {
            var source=Solid(4,4,25,25,25); var output=new byte[source.Length];
            new BackgroundMatteSession(4,4,Manual()).Process(source,output);
            Assert.That(output[3],Is.InRange(1,254)); Assert.Greater(output[0],source[0]);
            double actual=ToLinear(output[0])*output[3]/255.0;
            Assert.That(actual,Is.EqualTo(ToLinear(25)).Within(.0008));
        }

        [Test] public void TemporalStabilizationDoesNotLeaveTrailsOnNewBackground()
        {
            var options=Manual(); options.TemporalSmoothing=.8f;
            var session=new BackgroundMatteSession(4,4,options); var output=new byte[64];
            session.Process(Solid(4,4,25,25,25),output); int first=output[3];
            session.Process(Solid(4,4,26,26,26),output); int smoothed=output[3];
            var reference=new byte[64]; new BackgroundMatteSession(4,4,Manual()).Process(Solid(4,4,26,26,26),reference);
            Assert.That(smoothed,Is.InRange(first,reference[3]-1));
            session.Process(Solid(4,4,0,0,0),output); Assert.AreEqual(0,output[3]);
            session.Process(Solid(4,4,255,255,255),output); Assert.AreEqual(255,output[3]);
        }

        [Test] public void TransparentSourcePixelsRemainTransparentInsideProtection()
        {
            var source=Solid(4,4,0,0,0); source[3]=0;
            var options=Manual(); options.ProtectedRegions=new[]{new BackgroundKeepRegion(0,0,1,1)};
            var output=new byte[64]; new BackgroundMatteSession(4,4,options).Process(source,output);
            Assert.AreEqual(0,output[3]); Assert.AreEqual(255,output[7]);
        }

        [Test] public void CancelledFrameDoesNotStartWritingOutput()
        {
            var source=Solid(4,4,0,0,0); var output=Solid(4,4,20,30,40); var expected=(byte[])output.Clone();
            var session=new BackgroundMatteSession(4,4,Manual());
            Assert.Throws<OperationCanceledException>(()=>session.Process(source,output,new CancellationToken(true)));
            CollectionAssert.AreEqual(expected,output);
        }

        [Test] public void RejectsInvalidSettingsAndAliasedBuffers()
        {
            var options=Manual(); options.Threshold=float.NaN;
            Assert.Throws<ArgumentException>(()=>new BackgroundMatteSession(4,4,options));
            options=Manual(); options.ProtectedRegions=new[]{new BackgroundKeepRegion(.9f,0,.5f,1)};
            Assert.Throws<ArgumentException>(()=>new BackgroundMatteSession(4,4,options));
            Assert.Throws<ArgumentOutOfRangeException>(()=>new BackgroundMatteSession(9000,9000,Manual()));
            var source=Solid(4,4,0,0,0);
            Assert.Throws<ArgumentException>(()=>new BackgroundMatteSession(4,4,Manual()).Process(source,source));
        }

        private static BackgroundOptions Manual() => new BackgroundOptions{AutoDetect=false,BorderConnected=false};
        private static byte[] Solid(int w,int h,byte r,byte g,byte b)
        { var pixels=new byte[w*h*4]; for(int y=0;y<h;y++)for(int x=0;x<w;x++)Pixel(pixels,w,x,y,r,g,b); return pixels; }
        private static void Pixel(byte[] p,int w,int x,int y,byte r,byte g,byte b)
        { int i=(y*w+x)*4;p[i]=r;p[i+1]=g;p[i+2]=b;p[i+3]=255; }
        private static byte Alpha(byte[] p,int w,int x,int y) => p[(y*w+x)*4+3];
        private static double ToLinear(byte value) { double s=value/255.0;return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4); }
    }
}
