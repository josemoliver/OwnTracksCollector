using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TrackViewer.Pages;

[IgnoreAntiforgeryToken]
public class LoginModel : PageModel
{
    public string  ReturnUrl  { get; private set; } = "/";
    public bool    ShowError  { get; private set; }

    public void OnGet(string? returnUrl = null, string? error = null)
    {
        ReturnUrl = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
        ShowError = error == "1";
    }
}
