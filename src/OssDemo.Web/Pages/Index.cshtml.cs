using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OssDemo.Web.Pages;

public class IndexModel : PageModel
{
    private readonly OperationalDataService operationalData;

    public IndexModel(OperationalDataService operationalData)
    {
        this.operationalData = operationalData;
    }

    public OperationalDashboard? Dashboard { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Dashboard = await operationalData.GetDashboardAsync(cancellationToken);
    }
}
