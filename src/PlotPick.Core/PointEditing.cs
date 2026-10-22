namespace PlotPick.Core;

public record InsertionPosition(int Index,string Message)
{
    public bool Valid=>Index>=0;
}
public static class PointEditing
{
    public static bool CanEdit(Series series)=>!series.Locked&&series.Kind!=SeriesKind.Derived;
    public static string Segment(DataPoint p)=>p.Note.StartsWith("segment:")?p.Note.Split('\n')[0]:"";
    public static HashSet<Guid> Select(Series series,Region box)=>series.Points.Where(p=>box.Contains(p.Pixel)).Select(p=>p.Id).ToHashSet();
    public static InsertionPosition FindInsertion(Series series,Vec pixel,double zoom)
    {
        if(!CanEdit(series))return new(-1,"当前系列不可编辑");
        if(series.Kind!=SeriesKind.Curve)return new(-1,"散点和柱图请手动指定插入位置");
        if(series.Points.Count<2)return new(-1,"不足两个点，请选择系列开头或结尾");
        var candidates=new List<(int Index,double Distance)>();
        for(int i=1;i<series.Points.Count;i++)
        {
            var a=series.Points[i-1];var b=series.Points[i];if(Segment(a)!=Segment(b))continue;
            var ab=b.Pixel-a.Pixel;double squared=ab.X*ab.X+ab.Y*ab.Y;if(squared<1e-12)continue;
            var ap=pixel-a.Pixel;double t=Math.Clamp((ap.X*ab.X+ap.Y*ab.Y)/squared,0,1);
            double distance=(pixel-(a.Pixel+ab*t)).Length*zoom;if(distance<=12)candidates.Add((i,distance));
        }
        if(candidates.Count==0)return new(-1,"附近没有可插入的连续线段，请手动指定位置");
        // Dense samples of one path create many nearby endpoint projections. Only
        // local distance minima represent distinct branch candidates.
        var distances=candidates.ToDictionary(c=>c.Index,c=>c.Distance);
        var minima=candidates.Where(c=>(!distances.TryGetValue(c.Index-1,out var previous)||c.Distance<=previous+1e-9)&&(!distances.TryGetValue(c.Index+1,out var next)||c.Distance<=next+1e-9)).ToList();
        var branches=new List<(int Index,double Distance)>();int lastIndex=-2;
        foreach(var candidate in minima){if(candidate.Index!=lastIndex+1)branches.Add(candidate);lastIndex=candidate.Index;}
        var ordered=branches.OrderBy(c=>c.Distance).ThenBy(c=>c.Index).ToList();var best=ordered[0];
        if(ordered.Skip(1).Any(c=>Math.Abs(c.Index-best.Index)>1&&c.Distance-best.Distance<3))return new(-1,"附近有多条路径，请手动指定前后位置");
        return new(best.Index,$"插入第 {best.Index} 与 {best.Index+1} 点之间");
    }
    public static DataPoint Insert(Series series,int index,Vec pixel)
    {
        if(!CanEdit(series))throw new InvalidOperationException("当前系列不可编辑。");
        if(index<0||index>series.Points.Count)throw new ArgumentOutOfRangeException(nameof(index));
        var neighbor=index>0?series.Points[index-1]:series.Points.FirstOrDefault();
        var point=new DataPoint{Pixel=pixel,Origin=PointOrigin.Manual,Review=ReviewState.Pending,Note=neighbor is null?"":Segment(neighbor)};
        series.Points.Insert(index,point);return point;
    }
    public static int Delete(Series series,IReadOnlySet<Guid> selected)
    {
        if(!CanEdit(series))throw new InvalidOperationException("当前系列不可编辑。");
        return series.Points.RemoveAll(p=>selected.Contains(p.Id));
    }
}
