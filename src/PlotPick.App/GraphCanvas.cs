using System.Windows;
using System.Windows.Input;
using PlotPick.Core;
using SkiaSharp;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace PlotPick.App;

public record CanvasView(double Scale,double OffsetX,double OffsetY);
public class GraphCanvas:FrameworkElement
{
    SKBitmap? surfaceBitmap;WriteableBitmap? surfaceImage;
    SKBitmap? bitmap,mask;double scale=1,ox,oy;Vec down,last;bool panning,boxing,addSelection;DataPoint? drag;
    public Plot? Plot {get;set;} public Series? CurrentSeries {get;set;}
    string tool="select";
    public string Tool {get=>tool;set{tool=value;drag=null;boxing=false;panning=false;ReleaseMouseCapture();InvalidateVisual();}}
    public string InspectionTitle {get;set;}="";
    public Vec? InspectionSample {get;set;}
    public bool SnapshotInspection {get;set;}
    public Func<Vec,InsertionPosition>? FindInsertion {get;set;}
    public CanvasView View=>new(scale,ox,oy);
    public void RestoreView(CanvasView view){scale=view.Scale;ox=view.OffsetX;oy=view.OffsetY;Hover?.Invoke(last,scale);InvalidateVisual();}
    public void ClearSelection(){Selected.Clear();SelectionUpdated?.Invoke(Selected);InvalidateVisual();}
    public void SelectRegion(Region region,bool append){var ids=CurrentSeries is null?new HashSet<Guid>():PointEditing.Select(CurrentSeries,region);if(append)Selected.UnionWith(ids);else Selected=ids;SelectionUpdated?.Invoke(Selected);InvalidateVisual();}
    public bool ContainsPixel(Vec p)=>bitmap is not null&&p.X>=0&&p.Y>=0&&p.X<bitmap.Width&&p.Y<bitmap.Height;
    public List<(string Color,List<DataPoint> Points)> PreviewLayers {get;set;}=[];
    public List<DataPoint> Preview {get;set;}=[];
    public List<Vec> Anchors {get;set;}=[];
    public HashSet<Guid> Selected {get;set;}=[];
    public bool HideImage {get;set;} public bool ShowMask {get;set;}
    public bool OriginalOnly {get;set;}
    public Action<Vec>? PixelClicked;public Action<Region>? RegionSelected;public Action<DataPoint,Vec>? PointMoved;
    public Action<HashSet<Guid>>? SelectionUpdated;public Action<Vec,double>? Hover;
    public GraphCanvas()
    {
        Focusable=true;ClipToBounds=true;
        MouseWheel+=Wheel;MouseDown+=Down;MouseMove+=Move;MouseUp+=Up;
        SizeChanged+=(s,e)=>InvalidateVisual();MouseLeave+=(s,e)=>{InspectionSample=null;InvalidateVisual();};
    }
    public void SetImage(byte[] bytes,bool fit=true){bitmap?.Dispose();bitmap=SKBitmap.Decode(bytes);if(fit)Fit();else InvalidateVisual();}
    public void SetMask(byte[]? bytes){mask?.Dispose();mask=bytes is null?null:SKBitmap.Decode(bytes);InvalidateVisual();}
    public void Fit(){if(bitmap is null||ActualWidth<=0||ActualHeight<=0)return;scale=Math.Min((ActualWidth-48)/bitmap.Width,(ActualHeight-48)/bitmap.Height);ox=(ActualWidth-bitmap.Width*scale)/2;oy=(ActualHeight-bitmap.Height*scale)/2;Hover?.Invoke(new(),scale);InvalidateVisual();}
    public Vec Pixel(Point p)=>new((p.X-ox)/scale,(p.Y-oy)/scale);
    public void CenterOn(Vec p){ox=ActualWidth/2-p.X*scale;oy=ActualHeight/2-p.Y*scale;InvalidateVisual();}
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);var dpi=VisualTreeHelper.GetDpi(this);int w=Math.Max(1,(int)(ActualWidth*dpi.DpiScaleX)),h=Math.Max(1,(int)(ActualHeight*dpi.DpiScaleY));
        if(surfaceBitmap is null||surfaceBitmap.Width!=w||surfaceBitmap.Height!=h){surfaceBitmap?.Dispose();surfaceBitmap=new SKBitmap(new SKImageInfo(w,h,SKColorType.Bgra8888,SKAlphaType.Premul));surfaceImage=new WriteableBitmap(w,h,96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32,null);}
        using(var canvas=new SKCanvas(surfaceBitmap))Paint(canvas,(float)dpi.DpiScaleX);
        surfaceImage!.WritePixels(new Int32Rect(0,0,w,h),surfaceBitmap.GetPixels(),surfaceBitmap.ByteCount,surfaceBitmap.RowBytes);context.DrawImage(surfaceImage,new Rect(0,0,ActualWidth,ActualHeight));
    }
    void Paint(SKCanvas c,float ratio)
    {
        c.Clear(SKColor.Parse("#EAF0F6"));if(bitmap is null)return;
        c.Scale(ratio);c.Translate((float)ox,(float)oy);c.Scale((float)scale);
        using var paint=new SKPaint{IsAntialias=true};paint.Color=SKColors.White;c.DrawRect(0,0,bitmap.Width,bitmap.Height,paint);
        if(OriginalOnly){c.DrawBitmap(bitmap,0,0,new SKSamplingOptions(SKFilterMode.Linear));return;}
        if(ShowMask&&mask is not null)c.DrawBitmap(mask,0,0,new SKSamplingOptions(SKFilterMode.Linear));else if(!HideImage)c.DrawBitmap(bitmap,0,0,new SKSamplingOptions(SKFilterMode.Linear));
        float r=(float)(2.2/scale),stroke=(float)(1.5/scale);
        if(Plot is {} plot)
        {
            foreach(var series in plot.Series.Where(s=>s.Visible))
            {
                paint.Color=SKColor.Parse(series.Color);paint.StrokeWidth=stroke;
                if(series.Kind is SeriesKind.Curve or SeriesKind.Derived)
                {
                    using var path=new SKPathBuilder();string lastSegment="";int stride=Math.Max(1,series.Points.Count/30000);
                    for(int i=0;i<series.Points.Count;i+=stride){var p=series.Points[i];var xy=p.Value is Vec v&&plot.Axes.Calibrated?plot.Axes.ToPixel(v,series.RightAxis):p.Pixel;if(i==0||p.Note.StartsWith("segment:")&&p.Note!=lastSegment)path.MoveTo((float)xy.X,(float)xy.Y);else path.LineTo((float)xy.X,(float)xy.Y);lastSegment=p.Note;}
                    paint.Style=SKPaintStyle.Stroke;Export.DrawPath(c,path,paint);paint.Style=SKPaintStyle.Fill;
                }
                int dotStride=Math.Max(1,series.Points.Count/8000);
                for(int i=0;i<series.Points.Count;i++)
                {
                    var p=series.Points[i];bool selected=Selected.Contains(p.Id);if(!selected&&i%dotStride!=0)continue;
                    var xy=p.Value is Vec v&&plot.Axes.Calibrated?plot.Axes.ToPixel(v,series.RightAxis):p.Pixel;
                    paint.Color=selected?SKColor.Parse("#F43F5E"):SKColor.Parse(series.Color);
                    Export.DrawMarker(c,(float)xy.X,(float)xy.Y,selected?r*1.7f:r,series.Marker,paint);
                    if(p.Review==ReviewState.Pending&&(selected||i%12==0)){paint.Style=SKPaintStyle.Stroke;paint.Color=SKColor.Parse("#F59E0B");c.DrawCircle((float)xy.X,(float)xy.Y,r*1.8f,paint);paint.Style=SKPaintStyle.Fill;}
                    if(p.Error is {} error)foreach(var endpoint in new[]{error.XLow,error.XHigh,error.YLow,error.YHigh}.OfType<Vec>()){c.DrawLine((float)xy.X,(float)xy.Y,(float)endpoint.X,(float)endpoint.Y,paint);c.DrawCircle((float)endpoint.X,(float)endpoint.Y,r,paint);}
                }
            }
            if(plot.Axes.Calibrated)foreach(var axis in new[]{plot.Axes.X,plot.Axes.Y,plot.Axes.RightY}.OfType<Axis>())
            {
                paint.Color=SKColor.Parse("#8B5CF6");paint.Style=SKPaintStyle.Stroke;paint.StrokeWidth=stroke;c.DrawLine((float)axis.A.X,(float)axis.A.Y,(float)axis.B.X,(float)axis.B.Y,paint);foreach(var q in new[]{axis.A,axis.B})c.DrawCircle((float)q.X,(float)q.Y,r*2,paint);paint.Style=SKPaintStyle.Fill;
            }
            void Rect(Region region,SKColor color){paint.Color=color.WithAlpha(28);c.DrawRect((float)region.X,(float)region.Y,(float)region.Width,(float)region.Height,paint);paint.Color=color;paint.Style=SKPaintStyle.Stroke;c.DrawRect((float)region.X,(float)region.Y,(float)region.Width,(float)region.Height,paint);paint.Style=SKPaintStyle.Fill;}
            var options=CurrentSeries is null?plot.Options:SeriesOptions.Get(plot,CurrentSeries);if(options.Roi is Region roi)Rect(roi,SKColor.Parse("#2563EB"));foreach(var ex in options.Exclusions)Rect(ex,SKColor.Parse("#EF4444"));
            if(boxing)Rect(Box(down,last),SKColor.Parse("#2563EB"));
        }
        foreach(var layer in PreviewLayers){paint.Color=SKColor.Parse(layer.Color);paint.Style=SKPaintStyle.Stroke;paint.StrokeWidth=stroke;foreach(var point in layer.Points)c.DrawCircle((float)point.Pixel.X,(float)point.Pixel.Y,r*1.6f,paint);paint.Style=SKPaintStyle.Fill;}
        paint.Color=SKColor.Parse("#F59E0B");foreach(var p in Preview)c.DrawCircle((float)p.Pixel.X,(float)p.Pixel.Y,r,paint);
        paint.Color=SKColor.Parse("#8B5CF6");foreach(var p in Anchors){c.DrawLine((float)p.X-r*3,(float)p.Y,(float)p.X+r*3,(float)p.Y,paint);c.DrawLine((float)p.X,(float)p.Y-r*3,(float)p.X,(float)p.Y+r*3,paint);}
        var sample=InspectionSample??last;
        if(Tool=="insert"&&ContainsPixel(sample)&&FindInsertion?.Invoke(sample) is {Valid:true} insertion&&CurrentSeries is {} active)
        {
            paint.Color=SKColor.Parse("#16A34A");paint.StrokeWidth=(float)(2/scale);
            foreach(int index in new[]{insertion.Index-1,insertion.Index})if(index>=0&&index<active.Points.Count)
            {
                // Never preview a connection across a pre-existing segment boundary.
                if(index==insertion.Index&&index>0&&index<active.Points.Count&&PointEditing.Segment(active.Points[index-1])!=PointEditing.Segment(active.Points[index]))continue;
                var q=active.Points[index].Pixel;c.DrawLine((float)q.X,(float)q.Y,(float)sample.X,(float)sample.Y,paint);
            }
            c.DrawCircle((float)sample.X,(float)sample.Y,r*2,paint);
        }
        c.ResetMatrix();c.Scale(ratio);
        if((IsMouseOver||SnapshotInspection)&&ContainsPixel(sample)&&Tool is "point" or "insert" or "anchor" or "check" or "error" or "color" or "multi-color")
        {
            bool color=Tool is "color" or "multi-color";float width=(float)Math.Min(260,ActualWidth-16),height=(float)Math.Min(color?214:190,ActualHeight-16);
            if(width<100||height<110)return;float x=(float)ActualWidth-width-8,y=8;
            using var border=new SKPaint{Color=SKColors.White,IsAntialias=true};c.DrawRoundRect(x,y,width,height,8,8,border);
            using var face=SKTypeface.FromFamilyName("Microsoft YaHei");using var font=new SKFont(face,11);border.Color=SKColor.Parse("#334155");
            string title=color?"选色预览 · 点击应用":Tool=="insert"?FindInsertion?.Invoke(sample)?.Message??"补点预览":InspectionTitle.Length>0?InspectionTitle:"原图局部放大";
            var lines=title.Split('\n');float textY=y+17;foreach(var line in lines.Take(2)){c.DrawText(line,x+8,textY,SKTextAlign.Left,font,border);textY+=16;}
            float size=Math.Min(120,Math.Min(width-20,height-(color?82:54))),left=x+(width-size)/2,top=y+42;
            var target=new SKRect(left,top,left+size,top+size);int px=(int)sample.X,py=(int)sample.Y;
            c.Save();c.ClipRect(target);var source=new SKRect(px-14.5f,py-14.5f,px+15.5f,py+15.5f);c.DrawBitmap(bitmap,source,target,new SKSamplingOptions(SKFilterMode.Nearest));c.Restore();
            border.Color=SKColor.Parse("#2563EB");border.StrokeWidth=1;c.DrawLine(left+size/2,top,left+size/2,top+size,border);c.DrawLine(left,top+size/2,left+size,top+size/2,border);
            if(color){var picked=ColorAt(sample);border.Color=picked;c.DrawRoundRect(x+10,y+height-40,24,24,3,3,border);border.Color=SKColor.Parse("#334155");c.DrawText($"#{picked.Red:X2}{picked.Green:X2}{picked.Blue:X2}",x+43,y+height-27,SKTextAlign.Left,font,border);c.DrawText($"RGB {picked.Red}, {picked.Green}, {picked.Blue}",x+43,y+height-11,SKTextAlign.Left,font,border);}
        }
    }

    static Region Box(Vec a,Vec b)=>new(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Abs(a.X-b.X),Math.Abs(a.Y-b.Y));
    void Wheel(object s,MouseWheelEventArgs e){var pos=e.GetPosition(this);var p=Pixel(pos);scale=Math.Clamp(scale*(e.Delta>0?1.15:1/1.15),0.02,20);ox=pos.X-p.X*scale;oy=pos.Y-p.Y*scale;Hover?.Invoke(p,scale);InvalidateVisual();e.Handled=true;}
    void Down(object s,MouseButtonEventArgs e)
    {
        if(bitmap is null)return;Focus();CaptureMouse();down=last=Pixel(e.GetPosition(this));addSelection=Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        panning=e.ChangedButton==MouseButton.Middle||Keyboard.IsKeyDown(Key.Space);if(panning){Cursor=Cursors.Hand;return;}
        if(e.ChangedButton!=MouseButton.Left)return;
        if(Tool=="select")
        {
            drag=CurrentSeries is not null&&PointEditing.CanEdit(CurrentSeries)?CurrentSeries.Points.Where(p=>p.Value is null).MinBy(p=>(p.Pixel-down).Length):null;
            if(drag is not null&&(drag.Pixel-down).Length*scale<=10){if(!Keyboard.IsKeyDown(Key.LeftCtrl))Selected.Clear();Selected.Add(drag.Id);SelectionUpdated?.Invoke(Selected);}
            else{drag=null;boxing=true;}
        }
        else if(Tool is "roi" or "crop" or "exclude" or "box-select")boxing=true;
        else if(ContainsPixel(down))PixelClicked?.Invoke(down);
        InvalidateVisual();
    }
    void Move(object s,MouseEventArgs e)
    {
        var pos=e.GetPosition(this);var p=Pixel(pos);
        if(panning&&IsMouseCaptured){ox+=(p.X-last.X)*scale;oy+=(p.Y-last.Y)*scale;p=Pixel(pos);}
        last=p;InspectionSample=ContainsPixel(p)?p:null;Hover?.Invoke(p,scale);InvalidateVisual();
    }
    void Up(object s,MouseButtonEventArgs e)
    {
        if(panning){panning=false;Cursor=Cursors.Arrow;ReleaseMouseCapture();return;}
        var p=Pixel(e.GetPosition(this));
        if(drag is not null&&(p-down).Length*scale>2)PointMoved?.Invoke(drag,p);
        else if(boxing)
        {
            var box=Box(down,p);
            if(Tool is "select" or "box-select")SelectRegion(box,addSelection);
            else if(box.Width>3&&box.Height>3)RegionSelected?.Invoke(box);
        }
        drag=null;boxing=false;ReleaseMouseCapture();InvalidateVisual();
    }
    public SKColor ColorAt(Vec p)=>bitmap?.GetPixel(Math.Clamp((int)p.X,0,bitmap.Width-1),Math.Clamp((int)p.Y,0,bitmap.Height-1))??SKColors.Blue;
}



