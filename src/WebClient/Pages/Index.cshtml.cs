using System.ComponentModel.DataAnnotations;
using System.Net.Http;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebClient.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public IndexModel(ILogger<IndexModel> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            return Page();
        }

        [HttpGet]
        [Route("GetPayload")]
        public async Task<JsonResult> GetPayload(string payload)
        {
            OutputModel outputModel = null;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                using var client = _httpClientFactory.CreateClient();

                var greetingResponse = await client.GetAsync($"https://localhost:5003/send?payload={HttpUtility.UrlEncode(payload)}");

                outputModel = await JsonSerializer.DeserializeAsync<OutputModel>(
                    await greetingResponse.Content.ReadAsStreamAsync(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                HttpContext.Response.Headers.Add("TraceIdentifier", HttpContext.TraceIdentifier);
            }

            return new JsonResult(outputModel);
        }
        public IActionResult OnPost() => RedirectToPage("send", new { payload = Input.Username });


        public class InputModel
        {
            [Required]
            [Display(Name = "Enter message to be sent to device")]
            public string Username { get; set; }

        }
    }

    public record OutputModel(string Message);
}
