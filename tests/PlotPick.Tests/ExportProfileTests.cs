using ClosedXML.Excel;
using PlotPick.Core;
using Xunit;

namespace PlotPick.Tests;
public class ExportProfileTests
{
    static Project Fixture()
    {
        var p=new Plot{Axes=CoreTests.Axes(),Series=[]};
        foreach(var name in new[]{"A","B","C"})p.Series.Add(new(){Name=name,Points=Enumerable.Range(0,3).Select(i=>new DataPoint{Pixel=p.Axes.ToPixel(new(i,10+i)),Review=ReviewState.Accepted}).ToList()});
        return new(){Documents=[new(){Name="private-source.pdf",Doi="private-doi",Plots=[p]}]};
    }
    static ExportProfile Profile(ExportLayout layout,params string[] keys)=>new(){Layout=layout,Columns=keys.Select(k=>new ExportColumn{Key=k,Title=k,Enabled=true}).ToList()};
    [Fact]public void SelectionOrderAndNamesAreRespected()
    {
        var p=Fixture();var o=Profile(ExportLayout.Long,"y","x");o.Columns[0].Title="响应";
        var b=ConfiguredExport.Build(ConfiguredExport.Enumerate(p).Where(x=>x.Series.Name!="B"),o);
        Assert.Equal(2,b.SeriesCount);Assert.Equal(6,b.PointCount);Assert.Equal(new[]{"响应","x"},b.Tables[0].Headers);
        Assert.Equal(10,double.Parse(b.Tables[0].Rows[0][0]),8);Assert.DoesNotContain("private",Json.Serialize(b));
    }
    [Theory][InlineData(ExportLayout.Worksheets,3,2)][InlineData(ExportLayout.Pairs,1,6)][InlineData(ExportLayout.SharedX,1,4)][InlineData(ExportLayout.Long,1,2)]
    public void LayoutsPreserveSelectedShape(ExportLayout layout,int tables,int columns)
    {
        var b=ConfiguredExport.Build(ConfiguredExport.Enumerate(Fixture()),Profile(layout,"x","y"));Assert.Equal(tables,b.Tables.Count);Assert.All(b.Tables,t=>Assert.Equal(columns,t.Headers.Count));Assert.Equal(9,b.PointCount);
    }
    [Fact]public void SharedXRefusesSilentRowAlignmentAndDuplicateX()
    {
        var p=Fixture();var s=p.Plots.First().Series[1];s.Points.RemoveAt(1);
        Assert.Throws<ArgumentException>(()=>ConfiguredExport.Build(ConfiguredExport.Enumerate(p),Profile(ExportLayout.SharedX,"x","y")));
        var b=ConfiguredExport.Build(ConfiguredExport.Enumerate(p),Profile(ExportLayout.Pairs,"x","y"));Assert.Equal("",b.Tables[0].Rows[2][2]);
        s.Points.Add(Json.Clone(s.Points[0]));Assert.Throws<ArgumentException>(()=>ConfiguredExport.Build(ConfiguredExport.Enumerate(p),Profile(ExportLayout.SharedX,"x","y")));
    }
    [Fact]public void ResamplingIsExplicitAndDoesNotMutateSources()
    {
        var p=Fixture();p.Plots.First().Series[1].Points.RemoveAt(1);var before=Json.Serialize(p);var o=Profile(ExportLayout.SharedX,"x","y");o.Resample=true;o.ResampleStep=.5;
        var b=ConfiguredExport.Build(ConfiguredExport.Enumerate(p),o);Assert.Equal(5,b.Tables[0].Rows.Count);Assert.Equal(10.5,double.Parse(b.Tables[0].Rows[1][2]),8);Assert.Equal(before,Json.Serialize(p));
    }
    [Fact]public void XlsxAndDelimitedUseExactPreviewColumns()
    {
        var p=Fixture();var b=ConfiguredExport.Build(ConfiguredExport.Enumerate(p),Profile(ExportLayout.Pairs,"y","series"));var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
        try{ConfiguredExport.Write(b,path);using var book=new XLWorkbook(path);Assert.Single(book.Worksheets);var sheet=book.Worksheet(1);Assert.Equal(6,sheet.LastColumnUsed()!.ColumnNumber());Assert.Equal(b.Tables[0].Headers[0],sheet.Cell(1,1).GetString());Assert.Equal(10,sheet.Cell(2,1).GetDouble());Assert.Equal("A",sheet.Cell(2,2).GetString());Assert.DoesNotContain("private",ConfiguredExport.Delimited(b.Tables[0],','));}finally{File.Delete(path);}
    }
    [Fact]public void FiltersKeepOnlyReviewedPointsAndErrorFieldsAreExplicit()
    {
        var p=Fixture();var plot=p.Plots.First();var s=plot.Series[0];s.Points[0].Review=ReviewState.Pending;s.Points[1].Error=new(){YLow=plot.Axes.ToPixel(new(1,8)),YHigh=plot.Axes.ToPixel(new(1,16))};
        var o=Profile(ExportLayout.Long,"y","y_low","y_high","y_minus","y_plus");o.AcceptedOnly=true;
        var b=ConfiguredExport.Build(ConfiguredExport.Enumerate(p).Take(1),o);Assert.Equal(2,b.PointCount);var row=b.Tables[0].Rows[0];Assert.Equal(8,double.Parse(row[1]),8);Assert.Equal(5,double.Parse(row[4]),8);
    }
    [Fact]public void SeriesParametersAreIndependentWithSharedRegionOnly()
    {
        var p=Fixture();var plot=p.Plots.First();plot.Options.Roi=new(10,20,30,40);SeriesOptions.Initialize(p);plot.Series[0].Extraction!.Tolerance=12;plot.Series[1].Extraction!.Tolerance=99;
        Assert.Equal(12,SeriesOptions.Get(plot,plot.Series[0]).Tolerance);Assert.Equal(99,SeriesOptions.Get(plot,plot.Series[1]).Tolerance);Assert.Equal(plot.Options.Roi,SeriesOptions.Get(plot,plot.Series[1]).Roi);
        plot.Series[1].UseSharedRegion=false;plot.Series[1].Extraction!.Roi=new(50,50,20,20);Assert.NotEqual(SeriesOptions.Get(plot,plot.Series[0]).Roi,SeriesOptions.Get(plot,plot.Series[1]).Roi);
    }
    [Fact]public void ThreeCurveDemoExtractsDistinctSeries()
    {
        var image=Imaging.Demo();var results=new[]{"#2563EB","#0D9488","#F59E0B"}.Select(c=>Extraction.Run(image,SeriesKind.Curve,new(){Color=c,Roi=new(155,105,1090,640)})).ToList();
        Assert.All(results,r=>Assert.True(r.Points.Count>200));Assert.NotEqual(results[0].Points[100].Pixel,results[1].Points[100].Pixel);Assert.NotEqual(results[1].Points[100].Pixel,results[2].Points[100].Pixel);
    }
    [Fact]public void EmptySelectionAndZeroColumnsAreRejected()
    {
        Assert.Throws<ArgumentException>(()=>ConfiguredExport.Build([],new()));Assert.Throws<ArgumentException>(()=>ConfiguredExport.Build(ConfiguredExport.Enumerate(Fixture()),Profile(ExportLayout.Long)));
    }
    [Fact]public void DateColumnsStayIsoAndErrorAmplitudesUseDays()
    {
        var p=Fixture();var plot=p.Plots.First();plot.Axes.X.Scale=AxisScale.Date;plot.Axes.X.Min=45000;plot.Axes.X.Max=45010;
        var b=ConfiguredExport.Build(ConfiguredExport.Enumerate(p).Take(1),Profile(ExportLayout.Long,"x","y"));Assert.False(b.Tables[0].Numeric[0]);Assert.Contains("T",b.Tables[0].Rows[0][0]);Assert.True(b.Tables[0].Numeric[1]);
    }
    [Fact]public void VersionOneLoadsWithoutChangingOriginalAndProfileRoundTrips()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir is not null&&!File.Exists(Path.Combine(dir.FullName,"global.json")))dir=dir.Parent;
        var source=Path.Combine(dir!.FullName,"samples/响应曲线.plotpick");var bytes=File.ReadAllBytes(source);var p=ProjectStore.Load(source);Assert.Equal(2,p.Version);Assert.All(p.Plots.SelectMany(x=>x.Series),s=>Assert.NotNull(s.Extraction));Assert.Equal(bytes,File.ReadAllBytes(source));
        p.ExportProfile=Profile(ExportLayout.Pairs,"y","x");p.ExportProfile.SignificantDigits=6;p.ExportProfile.PadSignificantZeros=true;var target=Path.Combine(dir.FullName,"artifacts/test-results/v02-profile.plotpick");ProjectStore.Save(p,target);Assert.Equal(Json.Serialize(p.ExportProfile),Json.Serialize(ProjectStore.Load(target).ExportProfile));
    }
    [Theory]
    [InlineData(12345.6,3,false,"1.23E+04")]
    [InlineData(.00123456,3,false,"0.00123")]
    [InlineData(-9.999,3,false,"-10")]
    [InlineData(-9.999,3,true,"-10.0")]
    [InlineData(1.2,3,true,"1.20")]
    [InlineData(0,3,true,"0.00")]
    [InlineData(1.234e-200,3,true,"1.23E-200")]
    [InlineData(1.234e200,3,true,"1.23E+200")]
    [InlineData(9.99,1,false,"1E+01")]
    [InlineData(1.23456789,6,false,"1.23457")]
    public void SignificantDigitsRoundWithoutChangingOriginal(double value,int digits,bool pad,string expected)
    {
        var p=Fixture();p.Plots.First().Series[0].Points[0].Value=new(0,value);var before=Json.Serialize(p);
        var o=Profile(ExportLayout.Long,"y");o.SignificantDigits=digits;o.PadSignificantZeros=pad;
        Assert.Equal(expected,ConfiguredExport.Build(ConfiguredExport.Enumerate(p).Take(1),o).Tables[0].Rows[0][0]);Assert.Equal(before,Json.Serialize(p));
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(18)]
    public void RejectsInvalidSignificantDigits(int digits)
    {
        var o=Profile(ExportLayout.Long,"y");o.SignificantDigits=digits;Assert.Throws<ArgumentException>(()=>ConfiguredExport.Build(ConfiguredExport.Enumerate(Fixture()),o));
    }
    [Fact]public void AllDataWritersRetainRoundedValuesAndExcelZeros()
    {
        var p=Fixture();p.Plots.First().Series[0].Points[0].Value=new(0,1.2);var o=Profile(ExportLayout.Long,"y");o.SignificantDigits=3;o.PadSignificantZeros=true;
        var b=ConfiguredExport.Build(ConfiguredExport.Enumerate(p).Take(1),o);var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir is not null&&!File.Exists(Path.Combine(dir.FullName,"global.json")))dir=dir.Parent;
        string stem=Path.Combine(dir!.FullName,"artifacts/test-results/v021-digits");
        foreach(var ext in new[]{"csv","tsv","txt","json","xlsx"})ConfiguredExport.Write(b,stem+"."+ext);
        foreach(var ext in new[]{"csv","tsv","txt"})Assert.Equal("1.20",File.ReadAllLines(stem+"."+ext)[1]);
        Assert.Equal("1.20",Json.Deserialize<ExportBundle>(File.ReadAllText(stem+".json")).Tables[0].Rows[0][0]);
        using var book=new XLWorkbook(stem+".xlsx");Assert.Equal(1.2,book.Worksheet(1).Cell(2,1).GetDouble());Assert.Equal("0.00",book.Worksheet(1).Cell(2,1).Style.NumberFormat.Format);
    }
}
