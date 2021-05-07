using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using Serilog;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace SubredditNotifier
{
    public class Program
    {
        private static bool SigtermCalled { get; set; }

        private static ILogger Logger { get; set; }

        static async Task Main(string[] args)
        {
            Logger = new LoggerConfiguration()
                        .WriteTo.Console(outputTemplate:
                            "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                        .MinimumLevel.Information()
                        .CreateLogger();

            try
            {
                Logger.Information("Starting Proxy");
                await Exec(args);
            }
            catch (Exception e)
            {
                Logger.Fatal(e, "Unhandled Exception");
            }
        }

        static async Task Exec(string[] args)
        {

            AppDomain.CurrentDomain.ProcessExit += (object sender, EventArgs e) =>
            {
                Logger.Information("Shutting Down");
                SigtermCalled = true;
            };

            Logger.Information("SIGTERM handler registered");

            Logger.Information("Loading Configuration");

            var config = GetConfiguration();

            while (!SigtermCalled)
            {
                if (SigtermCalled)
                {
                    break;
                }

                await PollReddit(config);

                Logger.Debug("Sleeping for {seconds} seconds", config.PollingFrequency);
                await Task.Delay(TimeSpan.FromSeconds(config.PollingFrequency));
            }

            Logger.Information("Finished");
        }

        private static async Task PollReddit(Configuration config)
        {
            var urlToCheck = $"https://www.reddit.com/r/{config.Subreddit}/new/.json?count=100";

            var client = new HttpClient();
            var content = await client.GetStringAsync(urlToCheck);

            var redditResponse = JsonDocument.Parse(content);
            var posts = redditResponse.RootElement.GetProperty("data").GetProperty("children")
                                        .EnumerateArray().Select(c =>
                                        {
                                            var data = c.GetProperty("data");
                                            var title = data.GetProperty("title");
                                            var url = data.GetProperty("url");

                                            return new
                                            {
                                                id = data.GetProperty("id").GetString(),
                                                url = url.GetString(),
                                                title = title.GetString(),
                                            };
                                        }).ToList();

            var f4Regex = new Regex(config.RegexToMatch, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var notifiedPosts = System.IO.File.ReadAllLines(config.RegistryFile).ToList();
            var f4SomethingPosts = posts.Where(p => !notifiedPosts.Contains(p.id) && f4Regex.IsMatch(p.title)).ToList();

            foreach (var post in f4SomethingPosts)
            {
                Console.WriteLine($"{post.title} - {post.url}");

                var data = new StringContent(JsonSerializer.Serialize(post), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(config.HomeAssistantWebhook, data);

                notifiedPosts.Add(post.id);
            }

            if (notifiedPosts.Count > config.MaxNotifiedCount)
            {
                notifiedPosts = notifiedPosts.Skip(notifiedPosts.Count - config.MaxNotifiedCount).ToList();
            }

            System.IO.File.WriteAllLines(config.RegistryFile, notifiedPosts);
        }



        private static Configuration GetConfiguration()
        {

            var maxNotifiedCount = int.Parse(Environment.GetEnvironmentVariable("MAX_TRACKING_COUNT") ?? "200");
            var registryFile = Environment.GetEnvironmentVariable("REGISTRY_PATH") ?? "notifiedPosts.txt";
            var regexToMatch = Environment.GetEnvironmentVariable("REGEX") ?? ".*";
            var frequencyInSeconds = int.Parse(Environment.GetEnvironmentVariable("POLLING_FREQUENCY") ?? "60");

            var subreddit = Environment.GetEnvironmentVariable("SUBREDDIT");
            var homeAssistantWebhook = Environment.GetEnvironmentVariable("HA_WEBHOOK_URL");

            if (subreddit == null)
            {
                throw new Exception("Subreddit name is required");
            }

            if (homeAssistantWebhook == null)
            {
                throw new Exception("Webhook URL is required");
            }

            return new Configuration(
                       maxNotifiedCount,
                       frequencyInSeconds,
                       registryFile,
                       subreddit,
                       regexToMatch,
                       homeAssistantWebhook
                   );
        }


        private record Configuration(
            int MaxNotifiedCount,
            int PollingFrequency,
            string RegistryFile,
            string Subreddit,
            string RegexToMatch,
            string HomeAssistantWebhook
        );
    }

}
