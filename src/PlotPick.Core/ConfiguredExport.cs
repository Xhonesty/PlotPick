using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace PlotPick.Core;

public enum ExportLayout { Worksheets, Pairs, SharedX, Long }
public sealed class ExportColumn
{
    public string Key {get;set;}="";
    public string Title {get;set;}="";
    public bool Enabled {get;set;}
}
public sealed class ExportProfile
{
    public ExportLayout Layout {get;set;}=ExportLayout.Worksheets;
    public List<ExportColumn> Columns {get;set;}=ConfiguredExport.DefaultColumns();
    public bool AcceptedOnly {get;set;}
    public double XTolerance {get;set;}=1e-8;
    public bool Resample {get;set;}
    public double ResampleStep {get;set;}=0.1;
    public int SignificantDigits {get;set;}=17;
    public bool PadSignificantZeros {get;set;}
}
public record ExportSeries(Document Document,Plot Plot,Series Series);
public record ExportTable(string Name,List<string> Headers,List<bool> Numeric,List<string[]> Rows);
public record ExportBundle(List<ExportTable> Tables,int SeriesCount,int PointCount,string Notice);

/// <summary>The preview, clipboard and every export writer consume the same projected tables.</summary>
public static class ConfiguredExport
{
    public static List<ExportColumn> DefaultColumns()=>new (string Key,string Title)[]{
        ("x","X"),("y","Y"),("category","分类"),("series","系列名称"),("plot","图表名称"),("axis","Y 轴归属"),
        ("x_low","X 下端点"),("x_high","X 上端点"),("y_low","Y 下端点"),("y_high","Y 上端点"),
        ("x_minus","X 负向误差"),("x_plus","X 正向误差"),("y_minus","Y 负向误差"),("y_plus","Y 正向误差"),
        ("document","来源文件"),("page","页码"),("figure","图号"),("doi","DOI"),("origin","取数来源"),("review","检查状态"),("note","备注"),
        ("document_id","文档 ID"),("plot_id","图表 ID"),("series_id","系列 ID")
    }.Select(x=>new ExportColumn{Key=x.Key,Title=x.Title,Enabled=x.Key is "x" or "y"}).ToList();
    public static IEnumerable<ExportSeries> Enumerate(Project project)=>project.Documents.SelectMany(d=>d.Plots.SelectMany(p=>p.Series.Select(s=>new ExportSeries(d,p,s))));
    static bool Numeric(string key,ExportSeries s)=>key=="page"||key.StartsWith("x_")&&(!key.EndsWith("low")&&!key.EndsWith("high")||s.Plot.Axes.X.Scale!=AxisScale.Date)||key.StartsWith("y_")&&(!key.EndsWith("low")&&!key.EndsWith("high")||s.Plot.Axes.GetY(s.Series.RightAxis).Scale!=AxisScale.Date)||key=="x"&&s.Plot.Axes.X.Scale!=AxisScale.Date||key=="y"&&s.Plot.Axes.GetY(s.Series.RightAxis).Scale!=AxisScale.Date;
    static string Number(double n,ExportProfile options)
    {
        string value=n.ToString("G"+options.SignificantDigits,CultureInfo.InvariantCulture);
        if(!options.PadSignificantZeros||!double.IsFinite(n))return value;
        int exponent=value.IndexOf('E');string mantissa=exponent<0?value:value[..exponent];string tail=exponent<0?"":value[exponent..];
        string significant=new(mantissa.Where(char.IsAsciiDigit).SkipWhile(c=>c=='0').ToArray());
        int missing=options.SignificantDigits-Math.Max(1,significant.Length);
        if(missing>0)mantissa+=(mantissa.Contains('.')?"":".")+new string('0',missing);
        return mantissa+tail;
    }
    // Excel keeps numbers numeric; apply a matching format to retain explicitly padded zeros.
    static string ExcelFormat(string value)
    {
        int exponent=value.IndexOf('E');string mantissa=exponent<0?value:value[..exponent];int dot=mantissa.IndexOf('.');
        string format=dot<0?"0":"0."+new string('0',mantissa.Length-dot-1);
        return format+(exponent<0?"":"E+00");
    }
    static string Format(Axis a,double n,ExportProfile options)=>a.Scale==AxisScale.Date?a.Export(n):Number(n,options);
    static string Value(ExportSeries item,DataPoint point,string key,ExportProfile options)
    {
        var p=item.Plot;var s=item.Series;var d=item.Document;var v=s.ValueOf(point,p.Axes);var y=p.Axes.GetY(s.RightAxis);
        string End(Vec? pos,bool isX,bool delta=false)
        {
            if(pos is not Vec q)return "";var value=p.Axes.ToData(q,s.RightAxis);double n=isX?value.X:value.Y;
            return delta?Number(Math.Abs(n-(isX?v.X:v.Y)),options):Format(isX?p.Axes.X:y,n,options);
        }
        return key switch
        {
            "x"=>Format(p.Axes.X,v.X,options),"y"=>Format(y,v.Y,options),"category"=>point.Category,"series"=>s.Name,"plot"=>p.Name,"axis"=>s.RightAxis?"右 Y":"左 Y",
            "x_low"=>End(point.Error?.XLow,true),"x_high"=>End(point.Error?.XHigh,true),"y_low"=>End(point.Error?.YLow,false),"y_high"=>End(point.Error?.YHigh,false),
            "x_minus"=>End(point.Error?.XLow,true,true),"x_plus"=>End(point.Error?.XHigh,true,true),"y_minus"=>End(point.Error?.YLow,false,true),"y_plus"=>End(point.Error?.YHigh,false,true),
            "document"=>d.Name,"page"=>p.Page.ToString(CultureInfo.InvariantCulture),"figure"=>p.Figure,"doi"=>d.Doi,
            "origin"=>point.Origin switch{PointOrigin.Manual=>"手动",PointOrigin.Automatic=>"自动",PointOrigin.Corrected=>"修正",_=>"派生"},"review"=>point.Review==ReviewState.Accepted?"已检查":"待检查","note"=>point.Note,
            "document_id"=>d.Id.ToString(),"plot_id"=>p.Id.ToString(),"series_id"=>s.Id.ToString(),_=>throw new ArgumentException("未知导出字段："+key)
        };
    }
    static List<string> Unique(IEnumerable<string> names)
    {
        var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var result=new List<string>();
        foreach(var name in names){var n=name;int suffix=2;while(!used.Add(n))n=name+" ("+suffix+++ ")";result.Add(n);}return result;
    }
    public static ExportBundle Build(IEnumerable<ExportSeries> selection,ExportProfile options)
    {
        var items=selection.ToList();if(items.Count==0)throw new ArgumentException("请至少选择一个数据系列。");
        var cols=options.Columns.Where(c=>c.Enabled).ToList();if(cols.Count==0)throw new ArgumentException("请至少选择一列。");
        var known=DefaultColumns().Select(c=>c.Key).ToHashSet();if(cols.Any(c=>!known.Contains(c.Key)||string.IsNullOrWhiteSpace(c.Title))||cols.Select(c=>c.Key).Distinct().Count()!=cols.Count)throw new ArgumentException("字段名称不能为空，且字段不能重复。");
        if(options.SignificantDigits is <1 or >17)throw new ArgumentException("有效数字应为 1–17。");
        var filtered=new List<(ExportSeries Item,List<DataPoint> Points)>();int excluded=0;
        foreach(var item in items)
        {
            if(!item.Plot.Axes.Calibrated)throw new ArgumentException($"{item.Plot.Name} 尚未设置坐标。");
            if(item.Series.Stale)throw new ArgumentException($"{item.Series.Name} 的分析结果已失效，请重新生成。");
            item.Plot.Axes.Validate(item.Series.RightAxis);
            var points=item.Series.Points.Where(p=>!options.AcceptedOnly&&!item.Plot.FromBatch||p.Review==ReviewState.Accepted).ToList();excluded+=item.Series.Points.Count-points.Count;filtered.Add((item,points));
        }
        int pointCount=filtered.Sum(x=>x.Points.Count);if(pointCount==0)throw new ArgumentException("当前范围与检查状态筛选下没有可导出的数据。");
        string notice=excluded>0?$"已排除 {excluded} 个未检查点（含批量处理规则）。":"未过滤数据点。";
        var tables=new List<ExportTable>();
        ExportTable Single(ExportSeries item,List<DataPoint> points)=>new(item.Series.Name,Unique(cols.Select(c=>c.Title)),cols.Select(c=>Numeric(c.Key,item)).ToList(),points.Select(p=>cols.Select(c=>Value(item,p,c.Key,options)).ToArray()).ToList());
        if(options.Layout==ExportLayout.Worksheets)tables=filtered.Select(f=>Single(f.Item,f.Points)).ToList();
        else if(options.Layout==ExportLayout.Long)
        {
            tables.Add(new("数据",Unique(cols.Select(c=>c.Title)),cols.Select(c=>items.All(i=>Numeric(c.Key,i))).ToList(),filtered.SelectMany(f=>Single(f.Item,f.Points).Rows).ToList()));
        }
        else if(options.Layout==ExportLayout.Pairs)
        {
            var singles=filtered.Select(f=>Single(f.Item,f.Points)).ToList();int count=singles.Max(t=>t.Rows.Count);
            var rows=Enumerable.Range(0,count).Select(r=>singles.SelectMany(t=>r<t.Rows.Count?t.Rows[r]:Enumerable.Repeat("",cols.Count)).ToArray()).ToList();
            tables.Add(new("独立列对",Unique(singles.SelectMany(t=>t.Headers.Select(h=>t.Name+" · "+h))),singles.SelectMany(t=>t.Numeric).ToList(),rows));
            notice+=" 各系列按自身采集顺序排列；同一行不表示相同 X。";
        }
        else
        {
            if(!cols.Any(c=>c.Key=="x")||!cols.Any(c=>c.Key=="y"))throw new ArgumentException("共用 X 表格必须选择 X 和 Y 列。");
            if(!double.IsFinite(options.XTolerance)||options.XTolerance<0)throw new ArgumentException("X 匹配容差必须为有限非负数。");
            if(items.Any(i=>i.Series.Kind is SeriesKind.Bars or SeriesKind.HorizontalBars)||items.Select(i=>i.Plot.Axes.X.Scale).Distinct().Count()>1||items.Select(i=>i.Plot.Axes.X.Unit).Distinct().Count()>1)throw new ArgumentException("共用 X 需要一致的连续 X 轴类型和单位；分类柱图请使用独立列对。");
            var vectors=filtered.Select(f=>f.Points.Select(p=>f.Item.Series.ValueOf(p,f.Item.Plot.Axes)).ToArray()).ToArray();
            foreach(var list in vectors)if(list.Length==0||list.Zip(list.Skip(1)).Any(z=>z.First.X>=z.Second.X))throw new ArgumentException("共用 X 要求每条系列 X 严格递增且唯一；请显式排序或改用独立列对。");
            double[] grid=vectors[0].Select(v=>v.X).ToArray();double[][]? interpolated=null;
            if(options.Resample)
            {
                if(cols.Any(c=>c.Key is not ("x" or "y")))throw new ArgumentException("临时重采样仅支持 X、Y 列，不能推算原图误差或来源字段。");
                double step=options.ResampleStep;if(!double.IsFinite(step)||step<=0)throw new ArgumentException("重采样间距必须为正数。");
                double start=vectors.Max(v=>v[0].X),end=vectors.Min(v=>v[^1].X);double n=Math.Floor((end-start)/step)+1;
                if(n<1||n>1000000)throw new ArgumentException("系列无共同范围，或重采样点数超过 100 万。");
                grid=Enumerable.Range(0,(int)n).Select(i=>start+i*step).ToArray();interpolated=vectors.Select(list=>Interpolate(list,grid)).ToArray();notice+=" 已在共同范围内临时线性重采样，原始数据未修改，无外推。";
            }
            else if(vectors.Any(v=>v.Length!=grid.Length||v.Where((p,i)=>Math.Abs(p.X-grid[i])>options.XTolerance).Any()))throw new ArgumentException("各系列 X 不一致。请改用独立列对，或明确开启临时重采样；不会按行号强行对齐。");
            var xColumn=cols.First(c=>c.Key=="x");var rest=cols.Where(c=>c.Key!="x").ToList();
            var headers=Unique(new[]{xColumn.Title}.Concat(items.SelectMany(i=>rest.Select(c=>i.Series.Name+" · "+c.Title))));
            var numeric=new[]{Numeric("x",items[0])}.Concat(items.SelectMany(i=>rest.Select(c=>Numeric(c.Key,i)))).ToList();var rows=new List<string[]>();
            for(int r=0;r<grid.Length;r++)
            {
                var row=new List<string>{Format(items[0].Plot.Axes.X,grid[r],options)};
                for(int i=0;i<items.Count;i++)foreach(var c in rest)row.Add(interpolated is null?Value(items[i],filtered[i].Points[r],c.Key,options):Format(items[i].Plot.Axes.GetY(items[i].Series.RightAxis),interpolated[i][r],options));rows.Add(row.ToArray());
            }
            tables.Add(new("共用 X",headers,numeric,rows));
        }
        return new(tables,items.Count,pointCount,notice);
    }
    static double[] Interpolate(Vec[] values,double[] grid)
    {
        int j=0;return grid.Select(x=>{while(j<values.Length-2&&values[j+1].X<x)j++;if(values.Length==1)return values[0].Y;var a=values[j];var b=values[j+1];return a.Y+(b.Y-a.Y)*(x-a.X)/(b.X-a.X);}).ToArray();
    }
    static string Escape(string s,char sep)
    {
        if(s.Length>0&&"=+@-".Contains(s[0])&&!double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out _))s="'"+s;
        return s.Contains(sep)||s.IndexOfAny(['\r','\n','"'])>=0?"\""+s.Replace("\"","\"\"")+"\"":s;
    }
    public static string Delimited(ExportTable table,char separator)=>string.Join("\r\n",new[]{table.Headers.ToArray()}.Concat(table.Rows).Select(row=>string.Join(separator,row.Select(v=>Escape(v,separator)))));
    public static void Write(ExportBundle bundle,string path)
    {
        string ext=Path.GetExtension(path).ToLowerInvariant();
        if(ext==".xlsx")
        {
            using var book=new XLWorkbook();var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var table in bundle.Tables)
            {
                string raw=new(table.Name.Where(c=>!"[]:*?/\\".Contains(c)).ToArray());raw=raw.Trim('\'',' ');if(raw.Length==0)raw="数据";raw=raw[..Math.Min(25,raw.Length)];string name=raw;int i=2;while(!used.Add(name))name=raw+" ("+i+++ ")";
                var sheet=book.AddWorksheet(name);for(int c=0;c<table.Headers.Count;c++)sheet.Cell(1,c+1).Value=table.Headers[c];
                for(int r=0;r<table.Rows.Count;r++)for(int c=0;c<table.Headers.Count;c++){var value=table.Rows[r][c];if(table.Numeric[c]&&double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)){sheet.Cell(r+2,c+1).Value=n;sheet.Cell(r+2,c+1).Style.NumberFormat.Format=ExcelFormat(value);}else sheet.Cell(r+2,c+1).Value=value;}
                sheet.Row(1).Style.Font.Bold=true;sheet.Row(1).Style.Fill.BackgroundColor=XLColor.FromHtml("#EFF6FF");sheet.Columns().AdjustToContents(8,36);sheet.SheetView.FreezeRows(1);
            }
            book.SaveAs(path);
        }
        else if(ext==".json")File.WriteAllText(path,Json.Serialize(bundle),new UTF8Encoding(false));
        else
        {
            if(bundle.Tables.Count!=1)throw new ArgumentException("文本文件只能保存一张表，请选择长表或独立列对布局。");
            File.WriteAllText(path,Delimited(bundle.Tables[0],ext==".csv"?',':'\t'),new UTF8Encoding(true));
        }
    }
}
