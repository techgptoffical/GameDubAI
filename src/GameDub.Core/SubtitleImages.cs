using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GameDub.Core;

public static class SubtitleImages
{
    // Retain near-white glyph strokes with a nearby dark outline; reject broad
    // bright background areas. Independently implemented after studying SubLens.
    public static Bitmap BrightText(Bitmap source)
    {
        var rect = new Rectangle(0,0,source.Width,source.Height);
        var output = new Bitmap(source.Width,source.Height,PixelFormat.Format32bppArgb);
        var input = source.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        var target = output.LockBits(rect,ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        try
        {
            var src = new byte[Math.Abs(input.Stride)*source.Height]; Marshal.Copy(input.Scan0,src,0,src.Length);
            var dst = new byte[Math.Abs(target.Stride)*source.Height];
            int Luma(int x,int y) { var p=y*input.Stride+x*4; return (src[p]+src[p+1]+src[p+2])/3; }
            for(var y=0;y<source.Height;y++) for(var x=0;x<source.Width;x++)
            {
                var p=y*input.Stride+x*4; var v=Luma(x,y);
                var saturation=Math.Max(src[p],Math.Max(src[p+1],src[p+2]))-Math.Min(src[p],Math.Min(src[p+1],src[p+2]));
                var dark=v;
                for(var radius=1;radius<=3;radius+=2)
                {
                    dark=Math.Min(dark,Luma(Math.Max(0,x-radius),y)); dark=Math.Min(dark,Luma(Math.Min(source.Width-1,x+radius),y));
                    dark=Math.Min(dark,Luma(x,Math.Max(0,y-radius))); dark=Math.Min(dark,Luma(x,Math.Min(source.Height-1,y+radius)));
                }
                var ink=v>=175 && saturation<=80 && v-dark>=55;
                var q=y*target.Stride+x*4; dst[q]=dst[q+1]=dst[q+2]=(byte)(ink?0:255); dst[q+3]=255;
            }
            Marshal.Copy(dst,0,target.Scan0,dst.Length);
        }
        finally { source.UnlockBits(input); output.UnlockBits(target); }
        return output;
    }
    public static Bitmap Scale(Bitmap source,int factor)
    {
        var output=new Bitmap(source.Width*factor,source.Height*factor,PixelFormat.Format32bppArgb);
        using var g=Graphics.FromImage(output); g.InterpolationMode=InterpolationMode.HighQualityBicubic;
        g.DrawImage(source,new Rectangle(0,0,output.Width,output.Height)); return output;
    }
    public static double TextScore(string text)
    {
        var words=text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var letters=text.Count(char.IsLetter); var junk=text.Count(c=>!char.IsLetterOrDigit(c)&&!char.IsWhiteSpace(c)&&!".,!?'\"-:;".Contains(c));
        return letters + words.Count(w=>w.Count(char.IsLetter)>=2)*3 - junk*8 - words.Count(w=>w.Length>24)*20;
    }
    public static CaptureRegion Resolve(CaptureRegion region, int width,int height,GameProfile? profile)
    {
        if(profile is not { RegionReferenceWidth: >0, RegionReferenceHeight: >0 }) return region;
        return new((int)Math.Round(region.X*(double)width/profile.RegionReferenceWidth),(int)Math.Round(region.Y*(double)height/profile.RegionReferenceHeight),
            Math.Max(20,(int)Math.Round(region.Width*(double)width/profile.RegionReferenceWidth)),Math.Max(20,(int)Math.Round(region.Height*(double)height/profile.RegionReferenceHeight)));
    }
}
