@ModelType List(Of Foo.Bar)
@Imports System.Linq
@Code
    ViewData("Title") = "Home"
    Dim n As Integer = 3
End Code
<h1>@ViewData("Title")</h1>
@If Model.Count > 0 Then
    @<ul>
    @For Each item In Model
        @<li>@item.Name - @Html.ActionLink("x", "Edit", New With {.id = item.Id})</li>
    Next
    </ul>
Else
    @<p>None</p>
End If
@Section Scripts
    <script>var a = 1;</script>
End Section
