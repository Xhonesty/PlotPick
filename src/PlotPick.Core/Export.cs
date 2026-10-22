using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using SkiaSharp;
namespace PlotPick.Core;

public static class Export
{
    public static void DrawPath(SKCanvas canvas,SKPathBuilder builder,SKPaint paint){using var path=builder.Snapshot();canvas.DrawPath(path,paint);}
    static readonly string[] Header=["document","page","figure","plot","series","axis","category","x","y","x_low","x_high","y_low","y_high","origin","review","note","document_id","plot_id","series_id"];
    public static List<string[]> Rows(Project project,bool acceptedOnly=false)
    {
        var rows=new List<string[]>{Header};
        foreach(var d in project.Documents)foreach(var p in d.Plots)
        {
            if(!p.Axes.Calibrated){if(p.Series.Any(s=>s.Points.Count>0)&&!acceptedOnly&&!p.FromBatch)throw new ArgumentException($"{p.Name} 尚未标定。");continue;}
            foreach(var s in p.Series)
            {
                if(s.Stale)throw new ArgumentException($"派生系列 {s.Name} 已失效，请重新分析或删除后导出。");
                foreach(var point in s.Points.Where(q=>!(acceptedOnly||p.FromBatch)||q.Review==ReviewState.Accepted))
                {
                    var v=s.ValueOf(point,p.Axes);var y=p.Axes.GetY(s.RightAxis);
                    string Endpoint(Vec? pixel,bool x)=>pixel is Vec q ? x?p.Axes.X.Export(p.Axes.ToData(q,s.RightAxis).X):y.Export(p.Axes.ToData(q,s.RightAxis).Y):"";
                    rows.Add([d.Name,p.Page.ToString(),p.Figure,p.Name,s.Name,s.RightAxis?"right":"left",point.Category,p.Axes.X.Export(v.X),y.Export(v.Y),Endpoint(point.Error?.XLow,true),Endpoint(point.Error?.XHigh,true),Endpoint(point.Error?.YLow,false),Endpoint(point.Error?.YHigh,false),point.Origin.ToString(),point.Review.ToString(),point.Note,d.Id.ToString(),p.Id.ToString(),s.Id.ToString()]);
                }
            }
        }
        return rows;
    }
    public static string Delimited(Project p,char separator=',',bool acceptedOnly=false)=>string.Join("\r\n",Rows(p,acceptedOnly).Select(r=>string.Join(separator,r.Select(v=>Quote(v,separator)))));
    static string Quote(string s,char sep)
    {
        // Protect spreadsheet readers from formulas in user-supplied labels while preserving numeric negatives.
        if(s.Length>0&&"=+@-".Contains(s[0])&&!double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out _))s="'"+s;
        return s.Contains(sep)||s.Contains('"')||s.Contains('\n')||s.Contains('\r')?"\""+s.Replace("\"","\"\"")+"\"":s;
    }
    public static void Write(Project project,string path,bool acceptedOnly=false)
    {
        var ext=Path.GetExtension(path).ToLowerInvariant();var rows=Rows(project,acceptedOnly);
        if(ext==".xlsx")
        {
            using var book=new XLWorkbook();int index=0;
            foreach(var group in rows.Skip(1).GroupBy(r=>(r[16],r[17],r[18])))
            {
                var ws=book.AddWorksheet($"系列 {++index}");for(int j=0;j<Header.Length;j++)ws.Cell(1,j+1).Value=Header[j];
                int row=2;foreach(var data in group){for(int j=0;j<data.Length;j++){if(j>=7&&j<=12&&double.TryParse(data[j],NumberStyles.Float,CultureInfo.InvariantCulture,out var number))ws.Cell(row,j+1).Value=number;else ws.Cell(row,j+1).Value=data[j];}row++;}
                ws.Range(1,1,row-1,Header.Length).CreateTable();ws.Columns().AdjustToContents(8,45);ws.SheetView.FreezeRows(1);
            }
            var source=book.AddWorksheet("来源与处理参数");source.Cell(1,1).Value="文档";source.Cell(1,2).Value="标题";source.Cell(1,3).Value="DOI";source.Cell(1,4).Value="图表 / 参数";
            int r=2;foreach(var d in project.Documents)foreach(var plot in d.Plots)
            {
                var metadata=Json.Serialize(new{d.Id,Plot=plot.Id,plot.Name,plot.Page,plot.Dpi,plot.Crop,plot.Figure,plot.Notes,plot.Processing,plot.Axes,plot.Options,Series=plot.Series.Select(s=>new{s.Id,s.Name,s.Kind,s.RightAxis,s.Color,s.Processing,s.ParentSeries,s.Stale})});
                // Excel cells have a 32,767-character limit. Keep data rows separate from provenance.
                for(int offset=0;offset<metadata.Length;offset+=30000){source.Cell(r,1).Value=d.Name;source.Cell(r,2).Value=d.Title;source.Cell(r,3).Value=d.Doi;source.Cell(r,4).Value=metadata.Substring(offset,Math.Min(30000,metadata.Length-offset));r++;}
            }
            source.Columns(1,3).AdjustToContents();source.Column(4).Width=65;book.SaveAs(path);
        }
        else if(ext==".json")File.WriteAllText(path,Json.Serialize(new{project.Name,Columns=Header,Data=rows.Skip(1).ToArray(),Sources=project.Documents}),new UTF8Encoding(false));
        else File.WriteAllText(path,Delimited(project,ext==".csv"?',':'\t',acceptedOnly),new UTF8Encoding(true));
    }
    public static void Chart(Plot plot,string path,int width=1600,int height=1000)
    {
        if(!plot.Axes.Calibrated)throw new ArgumentException("请先标定坐标轴。");
        var ext=Path.GetExtension(path).ToLowerInvariant();
        using var stream=File.Create(path);
        if(ext==".pdf"){using var doc=SKDocument.CreatePdf(stream);var canvas=doc.BeginPage(width,height);DrawChart(canvas,plot,width,height);doc.EndPage();doc.Close();}
        else if(ext==".svg"){using var canvas=SKSvgCanvas.Create(SKRect.Create(width,height),stream);DrawChart(canvas,plot,width,height);}
        else{using var bitmap=new SKBitmap(width,height);using(var canvas=new SKCanvas(bitmap))DrawChart(canvas,plot,width,height);using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,100);data.SaveTo(stream);}
    }
    public static void DrawChart(SKCanvas c,Plot plot,int w,int h)
    {
        c.Clear(SKColors.White);float left=150,top=100,right=w-170,bottom=h-150;
        using var paint=new SKPaint{IsAntialias=true};using var font=new SKFont(SKTypeface.FromFamilyName("Microsoft YaHei"),22);using var titleFont=new SKFont(SKTypeface.FromFamilyName("Microsoft YaHei"),30);
        float Position(double value,Axis a,float start,float end)=>(float)(start+(end-start)*(a.Forward(value)-a.Forward(a.Min))/(a.Forward(a.Max)-a.Forward(a.Min)));
        Vec Map(Vec value,bool rightAxis)=>new(Position(value.X,plot.Axes.X,left,right),Position(value.Y,plot.Axes.GetY(rightAxis),bottom,top));
        for(int i=0;i<=5;i++)
        {
            float x=left+(right-left)*i/5,y=bottom-(bottom-top)*i/5;paint.Color=SKColor.Parse("#E2E8F0");paint.StrokeWidth=1;c.DrawLine(x,top,x,bottom,paint);c.DrawLine(left,y,right,y,paint);
            paint.Color=SKColor.Parse("#64748B");
            var a=plot.Axes.X;var b=plot.Axes.Y;c.DrawText(a.Format(a.Inverse(a.Forward(a.Min)+(a.Forward(a.Max)-a.Forward(a.Min))*i/5)),x-20,bottom+36,SKTextAlign.Left,font,paint);c.DrawText(b.Format(b.Inverse(b.Forward(b.Min)+(b.Forward(b.Max)-b.Forward(b.Min))*i/5)),15,y+8,SKTextAlign.Left,font,paint);
            if(plot.Axes.RightY is Axis ry)c.DrawText(ry.Format(ry.Inverse(ry.Forward(ry.Min)+(ry.Forward(ry.Max)-ry.Forward(ry.Min))*i/5)),right+20,y+8,SKTextAlign.Left,font,paint);
        }
        paint.Color=SKColor.Parse("#0F172A");c.DrawText(string.IsNullOrWhiteSpace(plot.ChartTitle)?plot.Name:plot.ChartTitle,left,55,SKTextAlign.Left,titleFont,paint);
        c.DrawText(plot.Axes.X.Label+" "+plot.Axes.X.Unit,(left+right)/2-35,h-40,SKTextAlign.Left,font,paint);c.DrawText(plot.Axes.Y.Label+" "+plot.Axes.Y.Unit,left,top-20,SKTextAlign.Left,font,paint);
        if(plot.Axes.RightY is Axis rightY)c.DrawText(rightY.Label+" "+rightY.Unit,right-70,top-20,SKTextAlign.Left,font,paint);
        int legend=0;
        foreach(var series in plot.Series.Where(s=>s.Visible))
        {
            paint.Color=SKColor.Parse(series.Color);paint.StrokeWidth=2.5f;paint.Style=SKPaintStyle.Fill;
            c.DrawCircle(left+legend*240,bottom+80,6,paint);c.DrawText(series.Name,left+16+legend*240,bottom+88,SKTextAlign.Left,font,paint);legend++;
            c.Save();c.ClipRect(new SKRect(left,top,right,bottom));
            using var path=new SKPathBuilder();string? lastSegment=null;
            foreach(var p in series.Points)
            {
                var xy=Map(series.ValueOf(p,plot.Axes),series.RightAxis);float x=(float)xy.X,y=(float)xy.Y;
                if(series.Kind is SeriesKind.Curve or SeriesKind.Derived){if(lastSegment is null||p.Note.StartsWith("segment:")&&p.Note!=lastSegment)path.MoveTo(x,y);else path.LineTo(x,y);lastSegment=p.Note;}
                else if(series.Kind is SeriesKind.Bars or SeriesKind.HorizontalBars)
                {
                    var baseData=p.BarBaseline is Vec bp?plot.Axes.ToData(bp,series.RightAxis):new Vec(series.ValueOf(p,plot.Axes).X,plot.Axes.GetY(series.RightAxis).Min);var baseline=Map(baseData,series.RightAxis);
                    c.DrawRect(series.Kind==SeriesKind.Bars?new SKRect(x-8,Math.Min(y,(float)baseline.Y),x+8,Math.Max(y,(float)baseline.Y)):new SKRect(Math.Min(x,(float)baseline.X),y-8,Math.Max(x,(float)baseline.X),y+8),paint);
                }
                else DrawMarker(c,x,y,5,series.Marker,paint);
                if(p.Error is {} e)foreach(var end in new[]{e.XLow,e.XHigh,e.YLow,e.YHigh}.OfType<Vec>()){var q=Map(plot.Axes.ToData(end,series.RightAxis),series.RightAxis);c.DrawLine(x,y,(float)q.X,(float)q.Y,paint);if(Math.Abs(q.X-x)>Math.Abs(q.Y-y))c.DrawLine((float)q.X,(float)q.Y-5,(float)q.X,(float)q.Y+5,paint);else c.DrawLine((float)q.X-5,(float)q.Y,(float)q.X+5,(float)q.Y,paint);}
            }
            paint.Style=SKPaintStyle.Stroke;if(series.Dashed)paint.PathEffect=SKPathEffect.CreateDash([8,5],0);DrawPath(c,path,paint);paint.PathEffect?.Dispose();paint.PathEffect=null;paint.Style=SKPaintStyle.Fill;c.Restore();
        }
    }
    public static void DrawMarker(SKCanvas c,float x,float y,float r,int shape,SKPaint p)
    {
        if(shape%3==0)c.DrawCircle(x,y,r,p);else if(shape%3==1)c.DrawRect(x-r,y-r,2*r,2*r,p);else {using var path=new SKPathBuilder();path.MoveTo(x,y-r);path.LineTo(x+r,y+r);path.LineTo(x-r,y+r);path.Close();DrawPath(c,path,p);}
    }
}


