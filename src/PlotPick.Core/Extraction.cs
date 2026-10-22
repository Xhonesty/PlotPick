using OpenCvSharp;
using SkiaSharp;

namespace PlotPick.Core;

public record ExtractionResult(List<DataPoint> Points, List<string> Warnings, byte[] Mask);
public static class Extraction
{
    public static ExtractionResult Run(byte[] image,SeriesKind kind,ExtractionOptions options,CancellationToken token=default,IProgress<double>? progress=null)
    {
        using var bitmap=SKBitmap.Decode(image)??throw new InvalidDataException("无法解码图片。");
        int w=bitmap.Width,h=bitmap.Height;
        var color=SKColor.Parse(options.Color);var mask=new byte[w*h];var roi=options.Roi??new Region(0,0,w,h);
        var pixels=bitmap.Pixels;
        for(int y=0;y<h;y++)
        {
            token.ThrowIfCancellationRequested();
            for(int x=0;x<w;x++)
            {
                var p=new Vec(x,y);if(!roi.Contains(p)||options.Exclusions.Any(r=>r.Contains(p)))continue;
                var c=pixels[y*w+x];
                double dr=c.Red-color.Red,dg=c.Green-color.Green,db=c.Blue-color.Blue;
                bool hit=options.IntensityMode ? (c.Red*0.299+c.Green*0.587+c.Blue*0.114)<options.Tolerance : dr*dr+dg*dg+db*db<options.Tolerance*options.Tolerance;
                if(c.Alpha>100 && hit)mask[y*w+x]=255;
            }
            if(y%100==0)progress?.Report(0.3*y/h);
        }
        using var mat=Mat.FromPixelData(h,w,MatType.CV_8UC1,mask);
        if(options.SuppressGrid)
        {
            using var vertical=new Mat();using var horizontal=new Mat();
            using var hk=Cv2.GetStructuringElement(MorphShapes.Rect,new Size(Math.Max(30,(int)roi.Width/2),1));
            using var vk=Cv2.GetStructuringElement(MorphShapes.Rect,new Size(1,Math.Max(30,(int)roi.Height/2)));
            Cv2.MorphologyEx(mat,horizontal,MorphTypes.Open,hk);Cv2.MorphologyEx(mat,vertical,MorphTypes.Open,vk);
            Cv2.Subtract(mat,horizontal,mat);Cv2.Subtract(mat,vertical,mat);
        }
        var points=new List<DataPoint>();var warnings=new List<string>();
        if(kind==SeriesKind.Scatter||kind==SeriesKind.Bars||kind==SeriesKind.HorizontalBars)
        {
            using var labels=new Mat();using var stats=new Mat();using var centroids=new Mat();
            int count=Cv2.ConnectedComponentsWithStats(mat,labels,stats,centroids);
            for(int i=1;i<count;i++)
            {
                token.ThrowIfCancellationRequested();int area=stats.At<int>(i,4);
                if(area<options.MinArea)continue;
                int left=stats.At<int>(i,0),top=stats.At<int>(i,1),width=stats.At<int>(i,2),height=stats.At<int>(i,3);
                bool large=area>options.MaxArea;
                if(kind==SeriesKind.Scatter)
                {
                    if(large){warnings.Add($"区域 ({left},{top}) 可能包含重叠点，请复核。");continue;}
                    var p=New(new(centroids.At<double>(i,0),centroids.At<double>(i,1)));
                    if(Math.Max(width,height)>Math.Min(width,height)*3)p.Note="形状狭长，请确认是否为散点";
                    points.Add(p);
                }
                else
                {
                    bool horizontal=kind==SeriesKind.HorizontalBars;
                    if(options.Baseline is null)throw new ArgumentException("柱状图需先在图上指定基线。");
                    double b=options.Baseline.Value;
                    double end=horizontal ? Math.Abs(left-b)>Math.Abs(left+width-1-b)?left:left+width-1 : Math.Abs(top-b)>Math.Abs(top+height-1-b)?top:top+height-1;
                    var p=New(horizontal?new(end,top+(height-1)/2.0):new(left+(width-1)/2.0,end));
                    p.BarBaseline=horizontal?new(b,p.Pixel.Y):new(p.Pixel.X,b);p.Category=$"分类 {points.Count+1}";points.Add(p);
                }
            }
            points=points.OrderBy(p=>kind==SeriesKind.HorizontalBars?p.Pixel.Y:p.Pixel.X).ToList();
        }
        else if(options.Seed is Vec traceSeed)
        {
            var traced=Trace(mat,roi,traceSeed,options.Step,token);points=traced.Points;warnings.AddRange(traced.Warnings);
        }
        else
        {
            int start=Math.Max(0,(int)roi.X),end=Math.Min(w-1,(int)(roi.X+roi.Width));
            double? last=options.Seed?.Y;int gap=0;int segment=0;
            if(options.Seed is Vec seed)start=Math.Clamp((int)seed.X,start,end);
            for(int x=start;x<=end;x+=Math.Max(1,options.Step))
            {
                token.ThrowIfCancellationRequested();var runs=new List<double>();int begin=-1;
                int yEnd=Math.Min(h,(int)(roi.Y+roi.Height));
                for(int y=Math.Max(0,(int)roi.Y);y<yEnd;y++)
                {
                    bool hit=mat.At<byte>(y,x)>0;
                    if(hit&&begin<0)begin=y;
                    if(begin>=0&&(!hit||y==yEnd-1)){runs.Add((begin+(hit?y:y-1))/2.0);begin=-1;}
                }
                if(runs.Count==0){gap+=Math.Max(1,options.Step);continue;}
                if(runs.Count>1)
                {
                    if(last is null){warnings.Add($"x={x} 存在多条路径；请使用起点或排除区域。");continue;}
                    var sorted=runs.OrderBy(y=>Math.Abs(y-last.Value)).ToList();
                    if(Math.Abs(Math.Abs(sorted[0]-last.Value)-Math.Abs(sorted[1]-last.Value))<3){warnings.Add($"x={x} 出现分支，已停止追踪；请重新指定起点。");break;}
                }
                double selected=last is null?runs[0]:runs.MinBy(y=>Math.Abs(y-last.Value));
                if(gap>options.MaxGap){segment++;warnings.Add($"x={x} 前有缺失段，未插值补全。");}
                var point=New(new(x,selected));point.Note=segment>0?$"segment:{segment}":"";points.Add(point);last=selected;gap=0;
                if(x%100<options.Step)progress?.Report(0.3+0.65*(x-start)/Math.Max(1.0,end-start));
            }
        }
        Cv2.ImEncode(".png",mat,out var preview);progress?.Report(1);
        if(points.Count==0)warnings.Add("未识别到数据。请调整颜色、容差或区域。");
        return new(points,warnings.Distinct().Take(100).ToList(),preview);
    }
    static DataPoint New(Vec p)=>new(){Pixel=p,Origin=PointOrigin.Automatic,Review=ReviewState.Pending};
    static (List<DataPoint> Points,List<string> Warnings) Trace(Mat source,Region roi,Vec seed,int sampleStep,CancellationToken token)
    {
        // Zhang-Suen thinning, followed by a bidirectional 8-neighbour skeleton walk.
        int left=Math.Max(0,(int)roi.X),top=Math.Max(0,(int)roi.Y),w=Math.Min(source.Width-left,(int)roi.Width),h=Math.Min(source.Height-top,(int)roi.Height);
        if(w<3||h<3)return([], ["识别区域过小。"]);
        var data=new byte[w*h];for(int y=0;y<h;y++)for(int x=0;x<w;x++)data[y*w+x]=source.At<byte>(y+top,x+left)>0?(byte)1:(byte)0;
        var remove=new List<int>();bool changed=true;
        for(int iteration=0;changed&&iteration<200;iteration++)
        {
            token.ThrowIfCancellationRequested();changed=false;
            for(int phase=0;phase<2;phase++)
            {
                remove.Clear();
                for(int y=1;y<h-1;y++)for(int x=1;x<w-1;x++)
                {
                    int i=y*w+x;if(data[i]==0)continue;
                    int p2=data[i-w],p3=data[i-w+1],p4=data[i+1],p5=data[i+w+1],p6=data[i+w],p7=data[i+w-1],p8=data[i-1],p9=data[i-w-1];
                    int count=p2+p3+p4+p5+p6+p7+p8+p9;if(count<2||count>6)continue;
                    int transitions=(p2==0&&p3==1?1:0)+(p3==0&&p4==1?1:0)+(p4==0&&p5==1?1:0)+(p5==0&&p6==1?1:0)+(p6==0&&p7==1?1:0)+(p7==0&&p8==1?1:0)+(p8==0&&p9==1?1:0)+(p9==0&&p2==1?1:0);
                    if(transitions!=1)continue;
                    bool cut=phase==0?p2*p4*p6==0&&p4*p6*p8==0:p2*p4*p8==0&&p2*p6*p8==0;if(cut)remove.Add(i);
                }
                if(remove.Count>0)changed=true;foreach(int i in remove)data[i]=0;
            }
        }
        var all=Enumerable.Range(0,data.Length).Where(i=>data[i]>0).ToArray();if(all.Length==0)return([], ["起点附近没有可追踪线条。"]);
        Vec Pixel(int i)=>new(i%w+left,i/w+top);
        int start=all.MinBy(i=>(Pixel(i)-seed).Length);
        if((Pixel(start)-seed).Length>30)return([], ["起点距识别线条超过 30 像素，请靠近曲线重新指定。"]);
        var visited=new HashSet<int>{start};var warnings=new List<string>();
        List<int> Neighbours(int i)
        {
            int x=i%w,y=i/w;var result=new List<int>();
            for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
            {
                if(dx==0&&dy==0||x+dx<0||x+dx>=w||y+dy<0||y+dy>=h)continue;
                int next=(y+dy)*w+x+dx;if(data[next]==0||visited.Contains(next))continue;
                // An orthogonal neighbour already represents this corner; avoid diagonal shortcuts.
                if(dx!=0&&dy!=0&&(data[y*w+x+dx]>0||data[(y+dy)*w+x]>0))continue;
                result.Add(next);
            }
            return result;
        }
        List<int> Walk(int next)
        {
            var result=new List<int>();int current=next;
            while(!visited.Contains(current))
            {
                token.ThrowIfCancellationRequested();visited.Add(current);result.Add(current);var candidates=Neighbours(current);
                if(candidates.Count==0)break;
                if(candidates.Count>1){warnings.Add($"在 ({Pixel(current).X:F0},{Pixel(current).Y:F0}) 检测到分支，已停止该方向追踪。");break;}
                current=candidates[0];
            }
            return result;
        }
        var ends=Neighbours(start);if(ends.Count>2)return([], ["起点处于分支，请在线条分离处选择起点。"]);
        var forward=ends.Count>0?Walk(ends[0]):[];var backward=ends.Count>1?Walk(ends[1]):[];backward.Reverse();var order=backward.Concat(new[]{start}).Concat(forward).ToList();
        var points=new List<DataPoint>();double distance=0;Vec? previous=null;
        foreach(int i in order){var p=Pixel(i);if(previous is Vec last)distance+=(p-last).Length;if(points.Count==0||distance>=Math.Max(1,sampleStep)){points.Add(New(p));distance=0;}previous=p;}
        if(order.Count>1&&points.Count>0&&points[^1].Pixel!=Pixel(order[^1]))points.Add(New(Pixel(order[^1])));
        return(points,warnings);
    }
    public static void DetectErrors(byte[] image,List<DataPoint> points,ExtractionOptions options,CancellationToken token,AxisSystem? axes=null,bool rightAxis=false)
    {
        using var bmp=SKBitmap.Decode(image);var c=SKColor.Parse(options.Color);
        bool Hit(int x,int y){if(x<0||y<0||x>=bmp.Width||y>=bmp.Height)return false;var b=bmp.GetPixel(x,y);return options.IntensityMode?b.Red+b.Green+b.Blue<options.Tolerance*3:Math.Pow(c.Red-b.Red,2)+Math.Pow(c.Green-b.Green,2)+Math.Pow(c.Blue-b.Blue,2)<options.Tolerance*options.Tolerance;}
        foreach(var p in points)
        {
            token.ThrowIfCancellationRequested();int x=(int)p.Pixel.X,y=(int)p.Pixel.Y;
            int Scan(int dx,int dy){int last=0,missing=0;for(int n=1;n<200;n++){if(Hit(x+dx*n,y+dy*n)){last=n;missing=0;}else if(++missing>3)break;}return last;}
            bool Cap(int distance,int dx,int dy)
            {
                if(distance<=5)return false;int ex=x+dx*distance,ey=y+dy*distance;
                int width=0;for(int k=-7;k<=7;k++)if(Hit(ex-dy*k,ey+dx*k)||Hit(ex-dy*k-dx,ey+dx*k-dy))width++;
                return width>=7;
            }
            int l=Scan(-1,0),r=Scan(1,0),u=Scan(0,-1),d=Scan(0,1);
            if(!Cap(l,-1,0))l=0;if(!Cap(r,1,0))r=0;if(!Cap(u,0,-1))u=0;if(!Cap(d,0,1))d=0;
            p.Error=new(){XLow=l>5?new(x-l,y):null,XHigh=r>5?new(x+r,y):null,YHigh=u>5?new(x,y-u):null,YLow=d>5?new(x,y+d):null};
            if(axes is not null)
            {
                var e=p.Error;var center=axes.ToData(p.Pixel,rightAxis);var xEnds=new[]{e.XLow,e.XHigh}.OfType<Vec>().OrderBy(q=>axes.ToData(q,rightAxis).X).ToArray();var yEnds=new[]{e.YLow,e.YHigh}.OfType<Vec>().OrderBy(q=>axes.ToData(q,rightAxis).Y).ToArray();
                e.XLow=xEnds.Where(q=>axes.ToData(q,rightAxis).X<center.X).Cast<Vec?>().FirstOrDefault();e.XHigh=xEnds.Where(q=>axes.ToData(q,rightAxis).X>center.X).Cast<Vec?>().LastOrDefault();
                e.YLow=yEnds.Where(q=>axes.ToData(q,rightAxis).Y<center.Y).Cast<Vec?>().FirstOrDefault();e.YHigh=yEnds.Where(q=>axes.ToData(q,rightAxis).Y>center.Y).Cast<Vec?>().LastOrDefault();
            }
            p.Review=ReviewState.Pending;p.Note="误差棒候选，请核对端帽及关联";
        }
    }
    public static List<string> Validate(Plot plot,Series series)
    {
        var issues=new List<string>();var seen=new HashSet<(int,int)>();Vec? previous=null;
        for(int i=0;i<series.Points.Count;i++)
        {
            var p=series.Points[i];if(!seen.Add(((int)Math.Round(p.Pixel.X),(int)Math.Round(p.Pixel.Y))))issues.Add($"点 {i+1}：重复或距离过近");
            if(p.Pixel.X<0||p.Pixel.Y<0||p.Pixel.X>plot.Width||p.Pixel.Y>plot.Height)issues.Add($"点 {i+1}：超出图像边界");
            try{series.ValueOf(p,plot.Axes);}catch(Exception ex){issues.Add($"点 {i+1}：{ex.Message}");}
            if(previous is Vec last&&(p.Pixel-last).Length>Math.Max(plot.Width,plot.Height)*0.2&&series.Kind==SeriesKind.Curve)issues.Add($"点 {i+1}：存在较大跳变");
            if(p.Review==ReviewState.Pending)issues.Add($"点 {i+1}：待复核");previous=p.Pixel;
        }
        foreach(var check in plot.Axes.Checks)
            issues.Add($"标定检查点残差：{(plot.Axes.ToPixel(check.Expected)-check.Pixel).Length:F3} px");
        return issues;
    }
}
