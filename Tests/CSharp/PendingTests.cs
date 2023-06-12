using System.Threading.Tasks;
using ICSharpCode.CodeConverter.Tests.TestRunners;
using Xunit;

namespace ICSharpCode.CodeConverter.Tests.CSharp.ExpressionTests;

public class PendingTests : ConverterTestBase
{

    [Fact]
    public async Task PendingTestAsync()
    {
        await TestConversionVisualBasicToCSharpAsync(@"Public Class Class1
    Sub Test(action As Action(Of String))
        action?(""toto"")
    End Sub
End Class", @"using System;

public partial class Class1
{
    public void Test(Action<string> action)
    {
        action?[""toto""];
    }
}");
    }

    [Fact]
    public async Task PendingTest1Async()
    {
        await TestConversionVisualBasicToCSharpAsync(@"Public Class Class1
    Public Sub Test(d1 As Date?, d2 As Date)
        If (d1 > d2) Then
        End If
    End Sub
End Class", @"using System;

public partial class Class1
{
    public void Test(DateTime? d1, DateTime d2)
    {
        if (d1 > d2)
        {
        }
    }
}");
    }

    [Fact]
    public async Task PendingTest2Async()
    {
        await TestConversionVisualBasicToCSharpAsync(@"Public Class Class1
    Public Sub Test()
        Dim d1 As Decimal? = 0
        Dim d2 As Decimal? = 0
        Dim d As Decimal? = d1 * d2
    End Sub
End Class", @"
public partial class Class1
{
    public void Test()
    {
        decimal? d1 = 0;
        decimal? d2 = 0;
        var d = d1 * d2;
    }
}");
    }

    [Fact]
    public async Task PendingTest3Async()
    {
        await TestConversionVisualBasicToCSharpAsync(@"Public Class Class1
    Public Sub Test()
        Dim d1 As Decimal? = 0
        Dim d2 As Decimal? = 0
        Dim d As Decimal = d1 * d2
    End Sub
End Class", @"
public partial class Class1
{
    public void Test()
    {
        decimal? d1 = 0;
        decimal? d2 = 0;
        var d = d1 * d2;
    }
}");
    }

    [Fact]
    public async Task PendingTest4Async()
    {
        await TestConversionVisualBasicToCSharpAsync(@"Public Class Class1
    Public Sub Test()
        Dim d As Decimal = CType(0.5, Decimal)
    End Sub
End Class", @"
public partial class Class1
{
    public void Test()
    {
        decimal d = 0.5d;
    }
}");
    }

    [Fact]
    public async Task PendingTest5Async()
    {
        await TestConversionVisualBasicToCSharpAsync(@"Public Class Class1
    Public Sub Test()
        Dim t = (Titi:=""Titi"", Toto:=""Toto"")
    End Sub
End Class", @"
public partial class Class1
{
    public void Test()
    {
        var t = (""Titi"", ""Toto"");
    }
}");
    }

    [Fact]
    public async Task PendingTest6Async()
    {
        await TestConversionVisualBasicToCSharpAsync(@"Class TestClass
    Private Sub TestMethod()
        For i = 1 To 2
            Dim b As Boolean
            Console.WriteLine(b)
            b = True
        Next
        For i = 1 To 2
            Dim b As Boolean = Nothing
            Console.WriteLine(b)
            b = True
        Next
    End Sub
End Class", @"using System;

internal partial class TestClass
{
    private void TestMethod()
    {
        var b = default(bool);
        for (int i = 1; i <= 2; i++)
        {
            Console.WriteLine(b);
            b = true;
        }
        for (int i = 1; i <= 2; i++)
        {
            bool b = default;
            Console.WriteLine(b);
            b = true;
        }
    }
}");
    }

    [Fact]
    public async Task PendingTest7Async()
    {
        await TestConversionVisualBasicToCSharpAsync(@"Class TestClass
    Private Sub TestMethod()
        Dim a As Action(Of String)
        a = Sub()
            End Sub
    End Sub
End Class", @"using System;

internal partial class TestClass
{
    private void TestMethod()
    {
        Action<string> a;
        a = new Action(() => { });
    }
}");
    }

    [Fact]
    public async Task PendingTest8Async()
    {
        await TestConversionVisualBasicToCSharpAsync(@"
Class TestClass
    Private Sub TestMethod(s1 As String, s2 As String)
        If s1 = s2 Then Return
    End Sub
End Class", @"
internal partial class TestClass
{
    private void TestMethod(string s1, string s2)
    {
        if ((s1 ?? "") == (s2 ?? ""))
        {
        }
    }
}");
    }

    [Fact]
    public async Task PendingTest9Async()
    {
        await TestConversionVisualBasicToCSharpAsync(@"
Class TestClass
    Private Const Toto As String = ""Toto""
    Private Sub TestMethod(s As String)
        If s = Toto Then Return
    End Sub
End Class", @"
internal partial class TestClass
{
    private void TestMethod(string s1, string s2)
    {
        if ((s1 ?? "") == (s2 ?? ""))
        {
        }
    }
}");
    }

    [Fact]
    public async Task PendingTest10Async()
    {
        await TestConversionVisualBasicToCSharpAsync(@"
Enum TestEnum
    One
    Two
    Three
End Enum
Class TestClass
    Private Sub TestMethod(s As String)
        If s = TestEnum.One.ToString() Then Return
    End Sub
End Class", @"
internal partial class TestClass
{
    private void TestMethod(string s1, string s2)
    {
        if ((s1 ?? "") == (s2 ?? ""))
        {
        }
    }
}");
    }

    [Fact]
    public async Task PendingTest11Async()
    {
        await TestConversionVisualBasicToCSharpAsync(@"
Class TestClass
    Private Sub TestMethod(s As String)
        Select Case s
        Case ""jkmlj""
        End Select
    End Sub
End Class", @"
internal partial class TestClass
{
    private void TestMethod(string s)
    {
        switch (s)
        {
            case ""jkmlj"":
                {
                    break;
                }
        }
    }
}");
    }

    [Fact]
    public async Task PendingTest11BisAsync()
    {
        await TestConversionVisualBasicToCSharpAsync(@"
Class TestClass
    Private Sub TestMethod(s As String)
        Select Case s
        Case """"
        End Select
    End Sub
End Class", @"using Cnr.GdpShared.StringExtensions;

internal partial class TestClass
{
    private void TestMethod(string s)
    {
        switch (s.NonNull())
        {
            case var @case when @case == "":
                {
                    break;
                }
        }
    }
}");
    }

    [Fact]
    public async Task PendingTest11TerAsync()
    {
        await TestConversionVisualBasicToCSharpAsync(@"
Class TestClass
    Private Sub TestMethod(s As String)
        Select Case s
        Case ""jkmlj""
        Case Else
        End Select
    End Sub
End Class", @"using Cnr.GdpShared.StringExtensions;

internal partial class TestClass
{
    private void TestMethod(string s)
    {
        switch (s.NonNull())
        {
            case ""jkmlj"":
                {
                    break;
                }

            default:
                {
                    break;
                }
        }
    }
}");
    }

    [Fact]
    public async Task PendingTest11QuadAsync()
    {
        await TestConversionVisualBasicToCSharpAsync(@"
Enum TestEnum
    One
    Two
    Three
End Enum
Class TestClass
    Private Sub TestMethod(s As String)
        Select Case s
        Case TestEnum.One.ToString()
        End Select
    End Sub
End Class", @"
internal partial class TestClass
{
    private void TestMethod(string s)
    {
        switch (s)
        {
            case ""jkmlj"":
                {
                    break;
                }
        }
    }
}");
    }
}