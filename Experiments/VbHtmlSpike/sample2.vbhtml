@ModelType MyApp.Models.OrderViewModel
@Code
    Layout = "~/Views/Shared/_Layout.vbhtml"
    ViewBag.Title = "Orders"
End Code

<h2>Orders for @Model.CustomerName</h2>

@Using Html.BeginForm("Save", "Orders", FormMethod.Post)
    @Html.AntiForgeryToken()
    @<div class="form-group">
        @Html.LabelFor(Function(m) m.CustomerName)
        @Html.TextBoxFor(Function(m) m.CustomerName, New With {.class = "form-control"})
    </div>
    @If Model.Lines IsNot Nothing AndAlso Model.Lines.Count > 0 Then
        @<table>
            @For Each line In Model.Lines
                @<tr>
                    <td>@line.Name</td>
                    <td>@(line.Qty * line.Price)</td>
                    <td>@(If(line.IsActive, "Yes", "No"))</td>
                </tr>
            Next
        </table>
    End If
    @<input type="submit" value="Save" />
End Using

@Section Scripts
    @Scripts.Render("~/bundles/jqueryval")
End Section
