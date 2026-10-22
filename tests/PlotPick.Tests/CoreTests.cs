using System.Diagnostics;
using System.IO.Compression;
using ClosedXML.Excel;
using PlotPick.Core;
using SkiaSharp;
using Xunit;

[assembly:CollectionBehavior(DisableTestParallelization=true)]
namespace PlotPick.Tests;
public class CoreTests
{
    static string Root=>FindRoot();
    static string FindRoot(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d is not null){if(File.Exists(Path.Combine(d.FullName,"global.json")))return d.FullName;d=d.Parent;}throw new Exception("Project root missing");}
    static string Artifact(string name){var dir=Path.Combine(Root,"artifacts","test-results");Directory.CreateDirectory(dir);return Path.Combine(dir,name);}
    public static AxisSystem Axes()=>new(){Calibrated=true,X=new(){A=new(100,700),B=new(1100,750),Min=0,Max=10},Y=new(){A=new(100,700),B=new(120,100),Min=-10,Max=100}};
    public static IEnumerable<object[]> Scales(){foreach(AxisScale x in Enum.GetValues<AxisScale>())foreach(AxisScale y in Enum.GetValues<AxisScale>())foreach(bool reverse in new[]{false,true})yield return [x,y,reverse];}
    [Theory][MemberData(nameof(Scales))]
    public void CoordinateRoundTrip(AxisScale x,AxisScale y,bool reverse)
    {
        var a=Axes();a.X.Scale=x;a.Y.Scale=y;a.X.Min=x==AxisScale.Date?45000:1;a.X.Max=x==AxisScale.Date?46000:100;a.Y.Min=y==AxisScale.Date?42000:0.1;a.Y.Max=y==AxisScale.Date?43000:1000;
        if(reverse){(a.X.Min,a.X.Max)=(a.X.Max,a.X.Min);(a.Y.Min,a.Y.Max)=(a.Y.Max,a.Y.Min);}
        for(int i=0;i<100;i++){var pixel=new Vec(150+i*8,150+i*4);Assert.True((a.ToPixel(a.ToData(pixel))-pixel).Length<0.01);}
    }
    [Fact]public void DoubleAxisIsIndependent(){var a=Axes();a.RightY=new(){A=new(1100,750),B=new(1120,150),Min=1,Max=1000,Scale=AxisScale.Logarithmic};var p=new Vec(600,400);Assert.NotEqual(a.ToData(p).Y,a.ToData(p,true).Y);Assert.True((a.ToPixel(a.ToData(p,true),true)-p).Length<0.01);}
    [Fact]public void RejectsDegenerateAndNonpositiveAxes(){var a=Axes();a.X.Min=a.X.Max;Assert.Throws<ArgumentException>(()=>a.ToData(new()));a=Axes();a.Y.B=a.Y.A+(a.X.B-a.X.A);Assert.Throws<ArgumentException>(()=>a.Validate());a=Axes();a.Y.Scale=AxisScale.Logarithmic;Assert.Throws<ArgumentException>(()=>a.Validate());}
    [Fact]public void DatesRequireExplicitFormat(){var a=new Axis{Scale=AxisScale.Date,DateFormat="yyyy-MM-dd"};Assert.Equal("2026-10-22T00:00:00",a.Export(a.Parse("2026-10-22")));Assert.Throws<FormatException>(()=>a.Parse("10/11/12"));}
    static Project MakeProject()
    {
        var project=new Project{Name="研究,测试"};var bytes=Imaging.Demo();var asset=project.AddAsset(bytes,".png");var p=new Plot{Name="实验图",ImageAsset=asset,OriginalAsset=asset,Width=1400,Height=900,Axes=Axes()};p.Series[0].Points=[new(){Pixel=new(500,400),Category="A,\"B\"",Error=new(){YLow=new(500,420),YHigh=new(500,380)}}];project.Documents.Add(new(){Name="paper.pdf",SourceAsset=asset,Doi="10.example/test",Plots=[p]});return project;
    }
    [Fact]public void PortableProjectAndUndoPreserveEverything()
    {
        var p=MakeProject();var path=Artifact("roundtrip.plotpick");ProjectStore.Save(p,path);var loaded=ProjectStore.Load(path);Assert.Equal(Json.Serialize(p),Json.Serialize(loaded));Assert.Equal(p.Assets.Single().Value,loaded.Assets.Single().Value);
        var history=new History();history.Push(p);p.Plots.First().Series[0].Points[0].Pixel=new(9,9);var old=history.Undo(p);Assert.Equal(new Vec(500,400),old.Plots.First().Series[0].Points[0].Pixel);Assert.Equal(new Vec(9,9),history.Redo(old).Plots.First().Series[0].Points[0].Pixel);
    }
    [Fact]public void AtomicSaveFailureKeepsOriginal()
    {
        var p=MakeProject();var path=Artifact("atomic.plotpick");ProjectStore.Save(p,path);var original=File.ReadAllBytes(path);p.Assets.Clear();Assert.Throws<InvalidDataException>(()=>ProjectStore.Save(p,path));Assert.Equal(original,File.ReadAllBytes(path));
    }
    [Fact]public void UnsupportedProjectIsRejected()
    {
        var p=MakeProject();p.Version=100;var path=Artifact("future.plotpick");ProjectStore.Save(p,path);Assert.Throws<InvalidDataException>(()=>ProjectStore.Load(path));var corrupt=Artifact("corrupt.plotpick");File.WriteAllText(corrupt,"broken");Assert.Throws<InvalidDataException>(()=>ProjectStore.Load(corrupt));
    }
    [Fact]public void XlsxCsvJsonPreserveNumbersAndErrors()
    {
        var p=MakeProject();var path=Artifact("data.xlsx");Export.Write(p,path);using var book=new XLWorkbook(path);var expected=p.Plots.First().Axes.ToData(new(500,400));Assert.Equal(expected.X,book.Worksheet(1).Cell(2,8).GetDouble(),10);Assert.NotEmpty(book.Worksheet(1).Cell(2,12).GetString());Assert.Contains("\"A,\"\"B\"\"\"",Export.Delimited(p));Export.Write(p,Artifact("data.json"));Export.Write(p,Artifact("data.csv"));
        p.Plots.First().Series[0].Points[0].Review=ReviewState.Pending;Assert.Single(Export.Rows(p,true));
    }
    [Theory][InlineData("移动平均")][InlineData("Savitzky–Golay")][InlineData("线性插值")][InlineData("线性拟合")][InlineData("多项式拟合")]
    public void AnalysisDoesNotMutateInputs(string method)
    {
        var input=Enumerable.Range(0,30).Select(i=>new Vec(i,2*i+3)).ToArray();var clone=input.ToArray();var (points,report)=Analysis.Process(input,method,method=="多项式拟合"?2:5);Assert.Equal(clone,input);Assert.NotEmpty(report);Assert.All(points,p=>Assert.True(double.IsFinite(p.Y)));if(method is "线性拟合" or "线性插值" or "多项式拟合")Assert.All(points,p=>Assert.InRange(Math.Abs(p.Y-(2*p.X+3)),0,1e-8));
    }
    [Fact]public void InterpolationRejectsDuplicatesAndUnsorted(){Assert.Throws<ArgumentException>(()=>Analysis.Process([new(1,2),new(1,3),new(2,4)],"线性插值"));Assert.Throws<ArgumentException>(()=>Analysis.Process([new(3,1),new(2,2),new(1,3)],"线性插值"));}
    [Fact]public void BatchRefusesDifferentAspectAndRequiresScaleReview(){var p=MakeProject().Plots.First();var t=BatchTemplate.From(p,p.Series[0]);var mapped=t.ForSize(2800,1800,false);Assert.False(mapped.Axes.Calibrated);Assert.Equal(p.Axes.X.A*2,mapped.Axes.X.A);Assert.True(t.ForSize(1400,900,true).Axes.Calibrated);Assert.Throws<ArgumentException>(()=>t.ForSize(1000,500,true));}
    [Theory][InlineData("−1.2",-1.2)][InlineData("1.5×10^3",1500)][InlineData("1e-3",0.001)]public void OcrNumericParser(string text,double expected)=>Assert.Equal(expected,Ocr.TryNumeric(text));
    [Fact]public void PdfAndAllImageExportsAreReadable()
    {
        var p=MakeProject().Plots.First();foreach(var ext in new[]{".png",".svg",".pdf"}){var path=Artifact("chart"+ext);Export.Chart(p,path);Assert.True(new FileInfo(path).Length>100);}
        var bytes=File.ReadAllBytes(Artifact("chart.pdf"));Assert.Equal(1,Pdf.PageCount(bytes));var page=Pdf.Render(bytes,0,72);using var decoded=SKBitmap.Decode(page);Assert.Equal(1600,decoded.Width);Assert.Throws<InvalidDataException>(()=>Pdf.PageCount([1,2,3]));
    }
    public static IEnumerable<object[]> Fixtures()=>Enumerable.Range(0,40).Select(i=>new object[]{i});
    [Theory][MemberData(nameof(Fixtures))]
    public void KnownTruthSyntheticExtraction(int fixture)
    {
        const int w=1000,h=650;bool scatter=fixture>=20;using var bitmap=new SKBitmap(w,h);using var canvas=new SKCanvas(bitmap);canvas.Clear(SKColors.White);using var paint=new SKPaint{Color=SKColor.Parse("#2563EB"),IsAntialias=true,StrokeWidth=2+(fixture%4)};
        var truth=new List<Vec>();
        double Curve(double x)=>h/2.0+Math.Sin(x/(80.0+fixture*3))*80+fixture;
        if(!scatter){using var path=new SKPathBuilder();for(int x=50;x<=950;x++){var y=Curve(x);truth.Add(new(x,y));if(x==50)path.MoveTo(x,(float)y);else path.LineTo(x,(float)y);}paint.Style=SKPaintStyle.Stroke;Export.DrawPath(canvas,path,paint);}
        else {var random=new Random(fixture);for(int i=0;i<40;i++){var p=new Vec(60+i%10*90+random.Next(-8,9),70+i/10*140+random.Next(-8,9));truth.Add(p);canvas.DrawCircle((float)p.X,(float)p.Y,4+fixture%3,paint);}}
        var bytes=Imaging.Png(bitmap);var dir=Path.Combine(Root,"samples","synthetic");Directory.CreateDirectory(dir);File.WriteAllBytes(Path.Combine(dir,$"{fixture:00}.png"),bytes);File.WriteAllText(Path.Combine(dir,$"{fixture:00}.json"),Json.Serialize(new{Type=scatter?"scatter":"curve",Points=truth}));
        var result=Extraction.Run(bytes,scatter?SeriesKind.Scatter:SeriesKind.Curve,new(){Roi=new(45,30,910,590),Step=4});
        if(scatter){var matches=result.Points.Count(p=>truth.Any(q=>(q-p.Pixel).Length<=1.5));Assert.True(matches/(double)truth.Count>=0.95);Assert.True(matches/(double)result.Points.Count>=0.95);}
        else{Assert.True(result.Points.Count>200);var errors=result.Points.Select(p=>Math.Abs(p.Pixel.Y-Curve(p.Pixel.X))).Order().ToArray();Assert.InRange(errors[errors.Length/2],0,1.5);}
    }
    [Fact]public void BarsSupportNegativeAndHorizontal()
    {
        using var bmp=new SKBitmap(600,500);using(var c=new SKCanvas(bmp)){c.Clear(SKColors.White);using var p=new SKPaint{Color=SKColor.Parse("#2563EB")};c.DrawRect(100,100,30,200,p);c.DrawRect(250,300,30,100,p);}
        var result=Extraction.Run(Imaging.Png(bmp),SeriesKind.Bars,new(){Baseline=300});Assert.Equal(2,result.Points.Count);Assert.Equal(100,result.Points[0].Pixel.Y);Assert.Equal(399,result.Points[1].Pixel.Y);Assert.All(result.Points,p=>Assert.NotNull(p.BarBaseline));
    }
    [Fact]public void ExportKeepsSameNamedSeriesSeparateAndSplitsLongMetadata()
    {
        var p=MakeProject();var plot=p.Plots.First();var second=Json.Clone(plot.Series[0]);second.Id=Guid.NewGuid();plot.Series.Add(second);plot.Notes=new string('a',40000);
        var path=Artifact("same-names.xlsx");Export.Write(p,path);using var book=new XLWorkbook(path);Assert.Equal(3,book.Worksheets.Count);Assert.Equal(2,book.Worksheet(1).LastRowUsed()!.RowNumber());Assert.Equal(2,book.Worksheet(2).LastRowUsed()!.RowNumber());Assert.True(book.Worksheet(3).LastRowUsed()!.RowNumber()>=3);
    }
    [Fact]public void ErrorDetectionRequiresCaps()
    {
        using var b=new SKBitmap(300,300);using(var c=new SKCanvas(b)){c.Clear(SKColors.White);using var p=new SKPaint{Color=SKColor.Parse("#2563EB"),StrokeWidth=3};c.DrawLine(100,50,100,150,p);c.DrawLine(90,50,110,50,p);c.DrawLine(90,150,110,150,p);c.DrawLine(200,50,200,150,p);}
        var points=new List<DataPoint>{new(){Pixel=new(100,100)},new(){Pixel=new(200,100)}};Extraction.DetectErrors(Imaging.Png(b),points,new(),CancellationToken.None);Assert.NotNull(points[0].Error!.YLow);Assert.NotNull(points[0].Error!.YHigh);Assert.Null(points[1].Error!.YLow);Assert.Null(points[1].Error!.YHigh);
        var axes=new AxisSystem{Calibrated=true,X=new(){A=new(0,0),B=new(300,0),Min=0,Max=300},Y=new(){A=new(0,0),B=new(0,300),Min=0,Max=300}};
        Extraction.DetectErrors(Imaging.Png(b),points,new(),CancellationToken.None,axes);Assert.True(points[0].Error!.YLow!.Value.Y<100);Assert.True(points[0].Error!.YHigh!.Value.Y>100);
        var oldEnd=points[0].Error!.YHigh!.Value;points[0].MoveTo(new(110,120));Assert.Equal(oldEnd+new Vec(10,20),points[0].Error!.YHigh!.Value);
    }
    [Fact]public void SeedTraceFollowsNonMonotonicCurve()
    {
        using var bitmap=new SKBitmap(500,500);using(var c=new SKCanvas(bitmap)){c.Clear(SKColors.White);using var p=new SKPaint{Color=SKColor.Parse("#2563EB"),Style=SKPaintStyle.Stroke,StrokeWidth=5};c.DrawCircle(250,250,150,p);}
        var result=Extraction.Run(Imaging.Png(bitmap),SeriesKind.Curve,new(){Seed=new(250,100),Step=4});Assert.True(result.Points.Count>150,$"Only {result.Points.Count} points: {string.Join(';',result.Warnings)}");Assert.True(result.Points.Min(p=>p.Pixel.X)<110&&result.Points.Max(p=>p.Pixel.X)>390);Assert.True(result.Points.Max(p=>p.Pixel.Y)>390);
    }
    [Fact]public void LongGapsAreNotFilledAndCancellationWorks()
    {
        using var bmp=new SKBitmap(600,400);using(var c=new SKCanvas(bmp)){c.Clear(SKColors.White);using var p=new SKPaint{Color=SKColor.Parse("#2563EB"),StrokeWidth=3};c.DrawLine(10,100,200,100,p);c.DrawLine(300,100,500,100,p);}
        var bytes=Imaging.Png(bmp);var result=Extraction.Run(bytes,SeriesKind.Curve,new());Assert.DoesNotContain(result.Points,p=>p.Pixel.X>205&&p.Pixel.X<295);Assert.Contains(result.Warnings,w=>w.Contains("缺失"));using var token=new CancellationTokenSource();token.Cancel();Assert.Throws<OperationCanceledException>(()=>Extraction.Run(bytes,SeriesKind.Curve,new(),token.Token));
    }
    [Fact]public void OcrNativeEngineLoadsAndRecognizesDigits()
    {
        using var b=new SKBitmap(600,150);using(var c=new SKCanvas(b)){c.Clear(SKColors.White);using var f=new SKFont(SKTypeface.FromFamilyName("Arial"),52);using var p=new SKPaint{Color=SKColors.Black,IsAntialias=true};c.DrawText("100 200 300",20,90,SKTextAlign.Left,f,p);}
        var result=Ocr.Recognize(Imaging.Png(b),Path.Combine(Root,"assets","tessdata"),CancellationToken.None);Assert.Contains(result,r=>r.Numeric==100);Assert.Contains(result,r=>r.Numeric==300);
    }
    [Fact]public void FourKExtractionBenchmark()
    {
        using var bitmap=new SKBitmap(3840,2160);using(var c=new SKCanvas(bitmap)){c.Clear(SKColors.White);using var p=new SKPaint{Color=SKColor.Parse("#2563EB"),StrokeWidth=4};c.DrawLine(100,1800,3700,200,p);}
        var bytes=Imaging.Png(bitmap);var sw=Stopwatch.StartNew();var result=Extraction.Run(bytes,SeriesKind.Curve,new(){Step=4});sw.Stop();File.WriteAllText(Artifact("benchmark.json"),Json.Serialize(new{Image="3840x2160",Milliseconds=sw.Elapsed.TotalMilliseconds,Points=result.Points.Count,Runtime=Environment.Version.ToString(),Processors=Environment.ProcessorCount}));Assert.True(result.Points.Count>800);Assert.True(sw.Elapsed.TotalSeconds<5,$"Extraction took {sw.Elapsed.TotalSeconds:F2}s");
    }
    [Fact]public void ProduceSampleProject()
    {
        var project=new Project{Name="响应曲线 · 示例研究"};var image=Imaging.Demo();var asset=project.AddAsset(image,".png");var p=new Plot{Name="响应随时间变化",Width=1400,Height=900,ImageAsset=asset,OriginalAsset=asset,Axes=new(){Calibrated=true,X=new(){A=new(150,750),B=new(1250,750),Min=0,Max=10,Label="Time (s)"},Y=new(){A=new(150,750),B=new(150,100),Min=0,Max=100,Label="Response (%)"}},Options=new(){Roi=new(155,105,1090,640)}};
        p.Series[0].Points=Extraction.Run(image,SeriesKind.Curve,p.Options).Points;project.Documents.Add(new(){Name="内置合成示例",SourceAsset=asset,Plots=[p]});ProjectStore.Save(project,Path.Combine(Root,"samples","响应曲线.plotpick"));
    }
    public static IEnumerable<object[]> RealFixtures()=>new[]{"iris","wine"}.SelectMany(n=>Enumerable.Range(0,10).Select(i=>new object[]{$"{n}-{i:00}"}));
    [Theory][MemberData(nameof(RealFixtures))]
    public void RealDataChartsDoNotCrashAndProduceReviewableOutput(string name)
    {
        var directory=Path.Combine(Root,"samples","real-data");using var metadata=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,name+".json")));var root=metadata.RootElement;var roi=Json.Deserialize<Region>(root.GetProperty("roi").GetRawText());var kind=root.GetProperty("kind").GetString() switch{"scatter"=>SeriesKind.Scatter,"bars"=>SeriesKind.Bars,_=>SeriesKind.Curve};
        var result=Extraction.Run(File.ReadAllBytes(Path.Combine(directory,name+".png")),kind,new(){Roi=roi,Baseline=roi.Y+roi.Height,MinArea=3,MaxArea=5000});Assert.NotEmpty(result.Points);Assert.All(result.Points,p=>Assert.Equal(ReviewState.Pending,p.Review));File.WriteAllText(Artifact(name+"-robustness.json"),Json.Serialize(new{Image=name,Count=result.Points.Count,result.Warnings,Scope="Robustness only, not accuracy acceptance"}));
    }
}


