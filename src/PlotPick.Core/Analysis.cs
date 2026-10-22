using MathNet.Numerics;
namespace PlotPick.Core;

public record Summary(int Count,double XMin,double XMax,double YMin,double YMax,double Mean,double StandardDeviation);
public static class Analysis
{
    public static Summary Summarize(Vec[] p)
    {
        if(p.Length==0)throw new ArgumentException("没有可分析的数据。");var mean=p.Average(x=>x.Y);
        return new(p.Length,p.Min(x=>x.X),p.Max(x=>x.X),p.Min(x=>x.Y),p.Max(x=>x.Y),mean,p.Length>1?Math.Sqrt(p.Sum(x=>Math.Pow(x.Y-mean,2))/(p.Length-1)):0);
    }
    public static (Vec[] Points,string Report) Process(Vec[] input,string method,int parameter=5,int samples=200)
    {
        if(input.Length<3)throw new ArgumentException("分析至少需要 3 个点。");
        if(input.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)))throw new ArgumentException("数据包含非有限值。");
        if(method=="移动平均")
        {
            int half=Math.Max(1,parameter/2);return (input.Select((p,i)=>new Vec(p.X,input.Skip(Math.Max(0,i-half)).Take(Math.Min(input.Length-1,i+half)-Math.Max(0,i-half)+1).Average(v=>v.Y))).ToArray(),$"移动平均，窗口 {2*half+1}（边界缩短）");
        }
        if(method=="Savitzky–Golay")
        {
            int size=Math.Min(input.Length,Math.Max(5,parameter|1));if(size%2==0)size--;
            if(input.Select(p=>p.X).Distinct().Count()!=input.Length)throw new ArgumentException("局部多项式平滑要求 X 不重复。");
            var result=input.Select((p,i)=>{int start=Math.Clamp(i-size/2,0,input.Length-size);var window=input.Skip(start).Take(size).ToArray();var coeff=Fit.Polynomial(window.Select(v=>v.X-p.X).ToArray(),window.Select(v=>v.Y).ToArray(),2);return new Vec(p.X,coeff[0]);}).ToArray();
            return(result,$"局部二阶多项式平滑，窗口 {size}，按实际 X 间距计算");
        }
        var sorted=input.OrderBy(p=>p.X).ToArray();
        if(method=="线性插值")
        {
            if(!input.SequenceEqual(sorted)||sorted.Zip(sorted.Skip(1)).Any(p=>p.First.X>=p.Second.X))throw new ArgumentException("插值要求 X 严格递增且不重复；请先显式排序或处理重复点。");
            var f=Interpolate.Linear(sorted.Select(p=>p.X),sorted.Select(p=>p.Y));
            return(Enumerable.Range(0,samples).Select(i=>{double x=sorted[0].X+(sorted[^1].X-sorted[0].X)*i/(samples-1);return new Vec(x,f.Interpolate(x));}).ToArray(),$"线性插值，{samples} 点，无外推");
        }
        int degree=method=="线性拟合"?1:Math.Clamp(parameter,2,5);
        if(input.Length<=degree||input.Select(p=>p.X).Distinct().Count()<=degree)throw new ArgumentException("独立 X 数量必须大于拟合阶数。");
        double center=(sorted[0].X+sorted[^1].X)/2,scale=(sorted[^1].X-sorted[0].X)/2;
        var coefficients=Fit.Polynomial(input.Select(p=>(p.X-center)/scale).ToArray(),input.Select(p=>p.Y).ToArray(),degree);
        double Predict(double x){double y=0,z=(x-center)/scale;for(int j=coefficients.Length-1;j>=0;j--)y=y*z+coefficients[j];return y;}
        double mean=input.Average(p=>p.Y),sse=input.Sum(p=>Math.Pow(p.Y-Predict(p.X),2)),sst=input.Sum(p=>Math.Pow(p.Y-mean,2));
        string r2=sst<1e-20?"不适用（Y 常数）":(1-sse/sst).ToString("G8");
        string equation=string.Join(" + ",coefficients.Select((v,i)=>$"({v:G8})·z^{i}"));
        return(Enumerable.Range(0,samples).Select(i=>{double x=sorted[0].X+(sorted[^1].X-sorted[0].X)*i/(samples-1);return new Vec(x,Predict(x));}).ToArray(),$"y = {equation}\nz = (x - {center:G10}) / {scale:G10}\n范围 [{sorted[0].X:G8}, {sorted[^1].X:G8}]\nR² = {r2}   RMSE = {Math.Sqrt(sse/input.Length):G8}\n原始数值空间拟合");
    }
}
