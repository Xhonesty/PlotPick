using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OpenCvSharp;
using SkiaSharp;
using Tesseract;
namespace PlotPick.Core;

public static class Imaging
{
    public static byte[] Png(SKBitmap bmp){using var image=SKImage.FromBitmap(bmp);using var data=image.Encode(SKEncodedImageFormat.Png,100);return data.ToArray();}
    public static byte[] Process(byte[] bytes,string operation,double angle=0)
    {
        using var mat=Cv2.ImDecode(bytes,ImreadModes.Color);if(mat.Empty())throw new InvalidDataException("图片解码失败。");using var output=new Mat();
        switch(operation)
        {
            case "灰度":Cv2.CvtColor(mat,output,ColorConversionCodes.BGR2GRAY);break;
            case "对比度":mat.ConvertTo(output,-1,1.2,-20);break;
            case "去噪":Cv2.MedianBlur(mat,output,3);break;
            case "旋转":Cv2.Rotate(mat,output,RotateFlags.Rotate90Clockwise);break;
            case "去倾斜":
                using(var transform=Cv2.GetRotationMatrix2D(new Point2f(mat.Width/2f,mat.Height/2f),angle,1))Cv2.WarpAffine(mat,output,transform,mat.Size(),InterpolationFlags.Linear,BorderTypes.Constant,Scalar.White);break;
            default:throw new ArgumentException("未知图像操作。");
        }
        Cv2.ImEncode(".png",output,out var result);return result;
    }
    public static byte[] Crop(byte[] bytes,Region roi)
    {
        using var mat=Cv2.ImDecode(bytes,ImreadModes.Color);
        int x=Math.Clamp((int)roi.X,0,mat.Width-1),y=Math.Clamp((int)roi.Y,0,mat.Height-1);
        using var cropped=new Mat(mat,new OpenCvSharp.Rect(x,y,Math.Clamp((int)roi.Width,1,mat.Width-x),Math.Clamp((int)roi.Height,1,mat.Height-y)));Cv2.ImEncode(".png",cropped,out var result);return result;
    }
    public static void TransformGeometry(Plot plot,Func<Vec,Vec> map)
    {
        foreach(var a in new[]{plot.Axes.X,plot.Axes.Y,plot.Axes.RightY}.OfType<Axis>()){a.A=map(a.A);a.B=map(a.B);}
        plot.Axes.Checks=plot.Axes.Checks.Select(c=>new CalibrationCheck(map(c.Pixel),c.Expected)).ToList();
        foreach(var s in plot.Series)foreach(var p in s.Points)
        {
            p.Pixel=map(p.Pixel);if(p.BarBaseline is Vec b)p.BarBaseline=map(b);
            if(p.Error is {} e){if(e.XLow is Vec a)e.XLow=map(a);if(e.XHigh is Vec c)e.XHigh=map(c);if(e.YLow is Vec d)e.YLow=map(d);if(e.YHigh is Vec f)e.YHigh=map(f);}
        }
        foreach(var options in plot.Series.Select(s=>s.Extraction).OfType<ExtractionOptions>().Append(plot.Options)){options.Roi=null;options.Exclusions.Clear();options.Seed=null;options.Baseline=null;}
    }
    public static byte[] Demo(int seed=0,bool scatter=false)
    {
        using var bmp=new SKBitmap(1400,900);using var c=new SKCanvas(bmp);c.Clear(SKColors.White);
        using var p=new SKPaint{IsAntialias=true};using var font=new SKFont(SKTypeface.FromFamilyName("Segoe UI"),24);
        for(int i=0;i<=10;i++)
        {
            p.Color=SKColor.Parse("#E8EDF3");p.StrokeWidth=1;c.DrawLine(150+i*110,100,150+i*110,750,p);c.DrawLine(150,750-i*65,1250,750-i*65,p);
            p.Color=SKColor.Parse("#64748B");c.DrawText(i.ToString(),140+i*110,790,SKTextAlign.Left,font,p);c.DrawText((i*10).ToString(),85,758-i*65,SKTextAlign.Left,font,p);
        }
        p.Color=SKColor.Parse("#1E293B");p.StrokeWidth=2;c.DrawLine(150,100,150,750,p);c.DrawLine(150,750,1250,750,p);
        c.DrawText("Time (s)",640,850,SKTextAlign.Left,font,p);c.DrawText("Response (%)",150,55,SKTextAlign.Left,font,p);
        var colors=new[]{"#2563EB","#0D9488","#F59E0B"};
        for(int s=0;s<3;s++)
        {
            p.Color=SKColor.Parse(colors[s]);p.StrokeWidth=3;using var path=new SKPathBuilder();
            for(int i=0;i<=220;i++)
            {
                double x=i/22.0,y=18+s*13+40*(1-Math.Exp(-x/(1.7+s*0.5)))+6*Math.Sin(x*1.3+seed*0.1+s);
                float px=150+i*5,py=(float)(750-y*6.5);
                if(i==0)path.MoveTo(px,py);else path.LineTo(px,py);
                if(scatter&&i%11==0)c.DrawCircle(px,py,6,p);
            }
            if(!scatter){p.Style=SKPaintStyle.Stroke;Export.DrawPath(c,path,p);p.Style=SKPaintStyle.Fill;}
        }
        return Png(bmp);
    }
}
public record OcrSuggestion(string Text,double? Numeric,float Confidence,Region Bounds);
public static partial class Ocr
{
    public static List<OcrSuggestion> Recognize(byte[] image,string modelDirectory,CancellationToken token)
    {
        if(!File.Exists(Path.Combine(modelDirectory,"eng.traineddata")))throw new FileNotFoundException("未找到离线 OCR 模型，请检查安装包的 tessdata 文件夹。");
        string languages=File.Exists(Path.Combine(modelDirectory,"chi_sim.traineddata"))?"eng+chi_sim":"eng";
        using var engine=new TesseractEngine(modelDirectory,languages,EngineMode.Default);
        using var pix=Pix.LoadFromMemory(image);using var page=engine.Process(pix,PageSegMode.SparseText);using var iter=page.GetIterator();var result=new List<OcrSuggestion>();
        iter.Begin();do
        {
            token.ThrowIfCancellationRequested();var text=iter.GetText(PageIteratorLevel.Word)?.Trim();
            if(string.IsNullOrWhiteSpace(text)||!iter.TryGetBoundingBox(PageIteratorLevel.Word,out var box))continue;
            result.Add(new(text,TryNumeric(text),iter.GetConfidence(PageIteratorLevel.Word),new(box.X1,box.Y1,box.Width,box.Height)));
        }while(iter.Next(PageIteratorLevel.Word));return result;
    }
    public static double? TryNumeric(string text)
    {
        string clean=text.Replace('−','-').Replace('–','-').Replace(" ","").Replace("×10^","e").Replace("x10^","e");
        if(double.TryParse(clean,NumberStyles.Float,CultureInfo.InvariantCulture,out var result)&&double.IsFinite(result))return result;
        return null;
    }
}
public static class Pdf
{
    static readonly object Gate=new();static bool initialized;
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern void FPDF_InitLibrary();
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern IntPtr FPDF_LoadMemDocument64(IntPtr buffer,nuint size,[MarshalAs(UnmanagedType.LPStr)]string? password);
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern uint FPDF_GetLastError();
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern IntPtr FPDF_LoadPage(IntPtr doc,int index);
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern double FPDF_GetPageWidth(IntPtr page);
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern double FPDF_GetPageHeight(IntPtr page);
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern void FPDF_ClosePage(IntPtr page);
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern IntPtr FPDFBitmap_CreateEx(int w,int h,int format,IntPtr buffer,int stride);
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern void FPDFBitmap_FillRect(IntPtr bitmap,int left,int top,int w,int h,uint color);
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern void FPDF_RenderPageBitmap(IntPtr bitmap,IntPtr page,int left,int top,int w,int h,int rotate,int flags);
    [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)]static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    static T WithDocument<T>(byte[] bytes,string? password,Func<IntPtr,T> action)
    {
        lock(Gate)
        {
            if(!initialized){FPDF_InitLibrary();initialized=true;}
            var handle=GCHandle.Alloc(bytes,GCHandleType.Pinned);IntPtr doc=IntPtr.Zero;
            try{doc=FPDF_LoadMemDocument64(handle.AddrOfPinnedObject(),(nuint)bytes.Length,password);if(doc==IntPtr.Zero)throw new InvalidDataException(FPDF_GetLastError()==4?"PDF 需要正确的密码。":"PDF 无法打开或文件损坏。");return action(doc);}
            finally{if(doc!=IntPtr.Zero)FPDF_CloseDocument(doc);handle.Free();}
        }
    }
    public static int PageCount(byte[] bytes,string? password=null)=>WithDocument(bytes,password,FPDF_GetPageCount);
    public static byte[] Render(byte[] bytes,int index,int dpi=300,string? password=null)=>WithDocument(bytes,password,doc=>
    {
        if(index<0||index>=FPDF_GetPageCount(doc))throw new ArgumentException("页码超出范围。");
        var page=FPDF_LoadPage(doc,index);if(page==IntPtr.Zero)throw new InvalidDataException("页面无法加载。");
        try
        {
            int w=(int)Math.Ceiling(FPDF_GetPageWidth(page)*dpi/72),h=(int)Math.Ceiling(FPDF_GetPageHeight(page)*dpi/72);
            if(w<1||h<1||(long)w*h>100_000_000)throw new InvalidDataException("页面过大，请降低渲染分辨率。");
            using var bitmap=new SKBitmap(new SKImageInfo(w,h,SKColorType.Bgra8888,SKAlphaType.Premul));var native=FPDFBitmap_CreateEx(w,h,4,bitmap.GetPixels(),bitmap.RowBytes);
            if(native==IntPtr.Zero)throw new InvalidDataException("PDF 渲染内存分配失败。");
            try{FPDFBitmap_FillRect(native,0,0,w,h,0xFFFFFFFF);FPDF_RenderPageBitmap(native,page,0,0,w,h,0,1);}finally{FPDFBitmap_Destroy(native);}
            return Imaging.Png(bitmap);
        }finally{FPDF_ClosePage(page);}
    });
}



