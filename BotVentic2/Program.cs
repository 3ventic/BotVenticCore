using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program
{
    static void Main(string[] args)
    {
        string token = Environment.GetEnvironmentVariable("TOKEN");
        string clientid = Environment.GetEnvironmentVariable("CLIENT_ID");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("Required environment variable TOKEN missing.");
        }
        else
        {
            Console.WriteLine($"Starting... Ctrl+C to stop");

            var bot = new BotVentic2.Bot(token, clientid);

            Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs e) =>
            {
                Console.WriteLine("Quitting...");
                e.Cancel = true;
                bot.QuitAsync().Wait();
                Environment.Exit(0);
            };

            bot.RunAsync().GetAwaiter().GetResult();
        }
    }

    private static readonly HttpClientHandler httpHandler = new HttpClientHandler()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        MaxConnectionsPerServer = 100,
        UseCookies = false,
        UseProxy = false
    };

    internal static async Task<string> RequestAsync(string uri, bool includeClientId = false)
    {
        string result = "";
        try
        {
            using var client = new HttpClient(httpHandler, false);
            if (includeClientId)
            {
                client.DefaultRequestHeaders.Add("Client-ID", "4wck2d3bifbikv779pnez14jujeyash");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.twitchtv.v5+json");
            }
            client.Timeout = TimeSpan.FromSeconds(60);
            result = await client.GetStringAsync(uri);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error with request to {uri}: {ex.ToString()}");
        }
        return result;
    }
}
