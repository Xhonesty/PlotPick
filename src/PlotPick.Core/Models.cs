using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlotPick.Core;

public enum AxisScale { Linear, Logarithmic, Date }
public enum SeriesKind { Curve, Scatter, Bars, HorizontalBars, Derived }
public enum PointOrigin { Manual, Automatic, Corrected, Derived }
public enum ReviewState { Pending, Accepted }
public record struct Vec(double X, double Y)
{
    public static Vec operator +(Vec a, Vec b) => new(a.X+b.X,a.Y+b.Y);
    public static Vec operator -(Vec a, Vec b) => new(a.X-b.X,a.Y-b.Y);
    public static Vec operator *(Vec a, double s) => new(a.X*s,a.Y*s);
    [JsonIgnore] public double Length => Math.Sqrt(X*X+Y*Y);
}
public record struct Region(double X, double Y, double Width, double Height)
{
    public bool Contains(Vec p) => p.X>=X && p.X<=X+Width && p.Y>=Y && p.Y<=Y+Height;
}
public class Axis
{
    public Vec A {get;set;}
    public Vec B {get;set;}
    public double Min {get;set;}
    public double Max {get;set;}=10;
    public AxisScale Scale {get;set;}
    public string Label {get;set;}="";
    public string Unit {get;set;}="";
    public string DateFormat {get;set;}="yyyy-MM-dd";
    public double Forward(double value) => Scale==AxisScale.Logarithmic ? value>0 ? Math.Log10(value) : throw new ArgumentException("对数轴的数值必须大于零。") : value;
    public double Inverse(double value) => Scale==AxisScale.Logarithmic ? Math.Pow(10,value) : value;
    public double Parse(string text)
    {
        double value=Scale==AxisScale.Date ? DateTime.ParseExact(text,DateFormat,CultureInfo.InvariantCulture).ToOADate() : double.Parse(text,CultureInfo.InvariantCulture);
        if(!double.IsFinite(value)) throw new ArgumentException("请输入有限数值。");
        Forward(value); return value;
    }
    public string Format(double value) => Scale==AxisScale.Date ? DateTime.FromOADate(value).ToString(DateFormat,CultureInfo.InvariantCulture) : value.ToString("G10",CultureInfo.InvariantCulture);
    public string Export(double value) => Scale==AxisScale.Date ? DateTime.FromOADate(value).ToString("yyyy-MM-ddTHH:mm:ss",CultureInfo.InvariantCulture) : value.ToString("G17",CultureInfo.InvariantCulture);
}
public class AxisSystem
{
    public bool Calibrated {get;set;}
    public Axis X {get;set;}=new(){Label="X"};
    public Axis Y {get;set;}=new(){Label="Y"};
    public Axis? RightY {get;set;}
    public List<CalibrationCheck> Checks {get;set;}=[];
    public Axis GetY(bool right) => right ? RightY ?? throw new ArgumentException("请先标定右侧 Y 轴。") : Y;
    public void Validate(bool right=false)
    {
        var y=GetY(right);
        foreach(var a in new[]{X,y})
        {
            if(!double.IsFinite(a.Min)||!double.IsFinite(a.Max)||a.Min==a.Max) throw new ArgumentException("轴的两个刻度必须是不同的有限数值。");
            a.Forward(a.Min);a.Forward(a.Max);
            if((a.B-a.A).Length<1) throw new ArgumentException("同一坐标轴的标定点距离过近。");
        }
        var u=X.B-X.A;var v=y.B-y.A;
        if(Math.Abs(Cross(u,v))/(u.Length*v.Length)<0.01) throw new ArgumentException("X 与 Y 轴方向不能平行。");
    }
    static double Cross(Vec a,Vec b)=>a.X*b.Y-a.Y*b.X;
    public Vec ToData(Vec p,bool right=false)
    {
        Validate(right);var y=GetY(right);var u=X.B-X.A;var v=y.B-y.A;var det=Cross(u,v);
        var tx=Cross(p-X.A,v)/det;var ty=Cross(u,p-y.A)/det;
        var result=new Vec(X.Inverse(X.Forward(X.Min)+tx*(X.Forward(X.Max)-X.Forward(X.Min))),y.Inverse(y.Forward(y.Min)+ty*(y.Forward(y.Max)-y.Forward(y.Min))));
        if(!double.IsFinite(result.X)||!double.IsFinite(result.Y))throw new ArgumentException("坐标超出可表示范围。");
        return result;
    }
    public Vec ToPixel(Vec p,bool right=false)
    {
        Validate(right);var y=GetY(right);var u=X.B-X.A;var v=y.B-y.A;
        var tx=(X.Forward(p.X)-X.Forward(X.Min))/(X.Forward(X.Max)-X.Forward(X.Min));
        var ty=(y.Forward(p.Y)-y.Forward(y.Min))/(y.Forward(y.Max)-y.Forward(y.Min));
        var offset=Cross(u,X.A-y.A)/Cross(u,v);
        return X.A+u*tx+v*(ty-offset);
    }
}
public record CalibrationCheck(Vec Pixel,Vec Expected);
public class ErrorExtent
{
    public Vec? XLow {get;set;} public Vec? XHigh {get;set;}
    public Vec? YLow {get;set;} public Vec? YHigh {get;set;}
}
public class DataPoint
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public Vec Pixel {get;set;}
    public Vec? Value {get;set;}
    public PointOrigin Origin {get;set;}=PointOrigin.Manual;
    public ReviewState Review {get;set;}=ReviewState.Accepted;
    public string Category {get;set;}="";
    public Vec? BarBaseline {get;set;}
    public ErrorExtent? Error {get;set;}
    public string Note {get;set;}="";
    public void MoveTo(Vec pixel)
    {
        var delta=pixel-Pixel;Pixel=pixel;
        if(Error is {} e){if(e.XLow is Vec a)e.XLow=a+delta;if(e.XHigh is Vec b)e.XHigh=b+delta;if(e.YLow is Vec c)e.YLow=c+delta;if(e.YHigh is Vec d)e.YHigh=d+delta;}
        Origin=PointOrigin.Corrected;Review=ReviewState.Pending;
    }
}
public class Series
{
    public ExtractionOptions? Extraction {get;set;}
    public bool UseSharedRegion {get;set;}=true;
    public bool ExtractSelected {get;set;}=true;
    [JsonIgnore] public string Symbol=>(Marker%3) switch {0=>"●",1=>"■",_=>"▲"};
    [JsonIgnore] public string Summary=>$"{Points.Count:N0} 点 · "+(Stale?"需重新分析":Points.Count==0?"尚未提取":Points.Any(p=>p.Review==ReviewState.Pending)?"待检查":"已检查")+(RightAxis?" · 右 Y":" · 左 Y");
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Name {get;set;}="系列 1";
    public string Color {get;set;}="#2563EB";
    public SeriesKind Kind {get;set;}
    public bool Visible {get;set;}=true;
    public bool Locked {get;set;}
    public bool RightAxis {get;set;}
    public int Marker {get;set;}
    public bool Dashed {get;set;}
    public List<DataPoint> Points {get;set;}=[];
    public string Processing {get;set;}="";
    public Guid? ParentSeries {get;set;}
    public bool Stale {get;set;}
    public Vec ValueOf(DataPoint point,AxisSystem axes)=>point.Value ?? axes.ToData(point.Pixel,RightAxis);
    public override string ToString()=>Name;
}
public class ExtractionOptions
{
    public string Color {get;set;}="#2563EB";
    public double Tolerance {get;set;}=65;
    public int Step {get;set;}=4;
    public int MaxGap {get;set;}=12;
    public int MinArea {get;set;}=8;
    public int MaxArea {get;set;}=3000;
    public bool SuppressGrid {get;set;}
    public bool IntensityMode {get;set;}
    public Region? Roi {get;set;}
    public List<Region> Exclusions {get;set;}=[];
    public Vec? Seed {get;set;}
    public double? Baseline {get;set;}
    public double? DataStep {get;set;}
}
public class Plot
{
    public bool FromBatch {get;set;}
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Name {get;set;}="图表 1";
    public int Page {get;set;}=1;
    public int Dpi {get;set;}=300;
    public string ImageAsset {get;set;}="";
    public string OriginalAsset {get;set;}="";
    public int Width {get;set;} public int Height {get;set;}
    public Region? Crop {get;set;}
    public string Processing {get;set;}="";
    public string Figure {get;set;}="";
    public string Notes {get;set;}="";
    public string ChartTitle {get;set;}="";
    public AxisSystem Axes {get;set;}=new();
    public ExtractionOptions Options {get;set;}=new();
    public List<Series> Series {get;set;}=[new()];
    public override string ToString()=>Name;
}
public class Document
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Name {get;set;}="";
    public string SourceAsset {get;set;}="";
    public string Title {get;set;}="";
    public string Doi {get;set;}="";
    public List<Plot> Plots {get;set;}=[];
}
public class Project
{
    public int Version {get;set;}=2;
    public ExportProfile? ExportProfile {get;set;}
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Name {get;set;}="未命名项目";
    public List<Document> Documents {get;set;}=[];
    [JsonIgnore] public Dictionary<string,byte[]> Assets {get;set;}=[];
    [JsonIgnore] public IEnumerable<Plot> Plots => Documents.SelectMany(d=>d.Plots);
    public string AddAsset(byte[] bytes,string extension)
    {
        var key="assets/"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()+extension;
        Assets.TryAdd(key,bytes);return key;
    }
}
public static class SeriesOptions
{
    public static ExtractionOptions Get(Plot plot,Series series)
    {
        var options=Json.Clone(series.Extraction??plot.Options);options.Color=series.Color;
        if(series.UseSharedRegion){options.Roi=plot.Options.Roi;options.Exclusions=Json.Clone(plot.Options.Exclusions);}
        return options;
    }
    public static void Initialize(Project project)
    {
        foreach(var p in project.Plots)foreach(var s in p.Series)
        {
            s.Extraction??=Json.Clone(p.Options);s.Extraction.Color=s.Color;
        }
    }
}
public static class Json
{
    public static readonly JsonSerializerOptions Options=new(){WriteIndented=true,Converters={new JsonStringEnumConverter()}};
    public static string Serialize<T>(T value)=>JsonSerializer.Serialize(value,Options);
    public static T Deserialize<T>(string value)=>JsonSerializer.Deserialize<T>(value,Options) ?? throw new InvalidDataException("项目数据为空。");
    public static T Clone<T>(T value)=>Deserialize<T>(Serialize(value));
}
public sealed class History
{
    readonly Stack<string> undo=new(),redo=new();
    public bool CanUndo=>undo.Count>0;public bool CanRedo=>redo.Count>0;
    public void Push(Project p){undo.Push(Json.Serialize(p));redo.Clear();}
    public Project Undo(Project p){if(!CanUndo)return p;redo.Push(Json.Serialize(p));return Restore(undo.Pop(),p);}
    public Project Redo(Project p){if(!CanRedo)return p;undo.Push(Json.Serialize(p));return Restore(redo.Pop(),p);}
    Project Restore(string state,Project p){var n=Json.Deserialize<Project>(state);n.Assets=p.Assets;return n;}
    public void Clear(){undo.Clear();redo.Clear();}
}
