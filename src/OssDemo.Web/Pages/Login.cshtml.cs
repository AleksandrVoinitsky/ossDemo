using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OssDemo.Web.Pages;

public class LoginModel : PageModel
{
    public const string UserName = "inspector";
    public const string Password = "Oos2026!";

    [BindProperty]
    public string Login { get; set; } = UserName;

    [BindProperty]
    public string PasswordInput { get; set; } = string.Empty;

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (Request.Cookies["oss.auth"] == "true")
        {
            return RedirectToPage("/Index");
        }

        Login = UserName;
        return Page();
    }

    public IActionResult OnPost()
    {
        if (string.Equals(Login, UserName, StringComparison.Ordinal) && PasswordInput == Password)
        {
            Response.Cookies.Append("oss.auth", "true", new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddHours(8)
            });

            return RedirectToPage("/Index");
        }

        ErrorMessage = "Неверный логин или пароль.";
        PasswordInput = string.Empty;
        return Page();
    }

    public IActionResult OnPostLogout()
    {
        Response.Cookies.Delete("oss.auth");
        return RedirectToPage("/Login");
    }
}
