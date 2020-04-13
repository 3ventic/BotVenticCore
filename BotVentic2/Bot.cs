using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Discord;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Discord.Rest;
using System.Linq;

namespace BotVentic2
{
    class Bot
    {
        public static readonly string MathQueryURL = Environment.GetEnvironmentVariable("MATHAPI_URL");

        private readonly DateTime _startedAt = DateTime.UtcNow;
        private DiscordSocketClient _client;
        private readonly string _token;
        private readonly string _clientid;
        private readonly ConcurrentDictionary<ulong, ulong> _lastHandledMessageOnChannel = new ConcurrentDictionary<ulong, ulong>();
        private readonly ConcurrentQueue<IMessage[]> _botReplies = new ConcurrentQueue<IMessage[]>();

        private List<EmoteInfo> _emotes = new List<EmoteInfo>();
        private string _bttvTemplate = "";

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Quality", "IDE0052:Remove unread private members", Justification = "Scoped here as readonly to prevent GC")]
        private readonly Timer _timer;

        private volatile int _commandsUsed = 0;
        private volatile int _emotesUsed = 0;
        private long _messagesReceived;
        private readonly object _messageLock = new object();


        private readonly EmbedAuthorBuilder _embedAuthor = new EmbedAuthorBuilder()
        {
            IconUrl = "https://i.3v.fi/raw/3logo.png",
            Name = "3v.fi",
            Url = "https://3v.fi/"
        };

        private string InviteUrl => $"https://discordapp.com/oauth2/authorize?client_id={_clientid}&scope=bot&permissions=19456";

        public Bot(string token, string clientid)
        {
            _token = token;
            _clientid = clientid;

            _timer = new Timer(async _ =>
            {
                Console.WriteLine("Updating emotes");
                var emotes = new List<EmoteInfo>();
                await UpdateTwitchEmotesAsync(emotes);
                await UpdateBttvEmotesAsync(emotes);
                await UpdateFFZEmotesAsync(emotes);
                _emotes = emotes;
                Console.WriteLine("Updated emotes");
                if (_client.ConnectionState == ConnectionState.Connected)
                {
                    await _client.SetGameAsync("3v.fi/l/botventic");
                }
            }, null, 0, 3600000);
        }

        internal async Task RunAsync()
        {
            _client = new DiscordSocketClient();

            _client.MessageReceived += Client_MessageReceivedAsync;
            _client.MessageUpdated += Client_MessageUpdatedAsync;
            _client.MessageDeleted += Client_MessageDeletedAsync;
            _client.LoggedOut += Client_LoggedOut;
            _client.Disconnected += Client_Disconnected;

            await _client.LoginAsync(TokenType.Bot, _token);
            await _client.StartAsync();
            await Task.Delay(-1);
        }

        private Task Client_Disconnected(Exception arg)
        {
            Console.WriteLine($"Disconnected due to {arg}");
            Environment.Exit(2);
            return Task.CompletedTask;
        }

        private Task Client_LoggedOut()
        {
            Console.WriteLine("Logged out.");

            return Task.Run(async () =>
            {
                await Task.Delay(500);
                Environment.Exit(1);
            });
        }
        internal async Task QuitAsync() => await _client.StopAsync();

        private async Task Client_MessageUpdatedAsync(Cacheable<IMessage, ulong> oldMessage, SocketMessage message, ISocketMessageChannel channel)
        {
            if (!message.Author.IsBot && message.Channel.Name[0] != '@')
            {
                string[] words = message.Content.Split(' ');
                (string reply, EmbedBuilder eReply) = await HandleCommandsAsync(words);
                if (reply == null && eReply == null)
                {
                    (reply, eReply) = HandleEmotesAndConversions(words);
                }

                if (reply != null || eReply != null)
                {
                    IMessage botMessage = GetExistingBotReplyOrNull(message.Id);
                    if (botMessage == null)
                    {
                        await SendReplyAsync(message, reply, eReply.Build());
                    }
                    else if (botMessage is IUserMessage botMsg)
                    {
                        await botMsg.ModifyAsync((msgProps) =>
                        {
                            msgProps.Content = reply;
                            msgProps.Embed = eReply.Build();
                        });
                    }
                }
            }
        }

        private async Task Client_MessageDeletedAsync(Cacheable<IMessage, ulong> message, ISocketMessageChannel channel)
        {
            ulong messageId = (await message.GetOrDownloadAsync()).Id;
            IMessage botMessage = GetExistingBotReplyOrNull(messageId);
            if (botMessage is IUserMessage botMsg)
            {
                await botMsg.DeleteAsync();
            }
        }

        private async Task Client_MessageReceivedAsync(SocketMessage message)
        {
            if (!message.Author.IsBot)
            {
                lock (_messageLock)
                {
                    _messagesReceived += 1;
                }
                if (message.Channel.Name[0] == '@')
                {
                    // Private message
                    await message.Channel.SendMessageAsync($"Add me to a server/guild: {InviteUrl}");
                }
                else
                {
                    string[] words = message.Content.Split(' ');
                    (string reply, EmbedBuilder eReply) = await HandleCommandsAsync(words);
                    if (reply == null && eReply == null)
                    {
                        (reply, eReply) = HandleEmotesAndConversions(words);
                    }

                    if (reply != null || eReply != null)
                    {
                        await SendReplyAsync(message, reply, eReply.Build());
                    }
                }
            }
        }

        private async Task SendReplyAsync(SocketMessage message, string reply, Embed embed = null)
        {
            try
            {
                _lastHandledMessageOnChannel[message.Channel.Id] = message.Id;
                RestUserMessage x = await message.Channel.SendMessageAsync(NullToEmpty(reply), embed: embed);
                AddBotReply(x, message);
                Console.WriteLine($"[Sent] [{x.Channel.Id}:{x.Channel.Name}] <{x.Author.Id}> {x.Author.Username}: {x.Content}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private (string, EmbedBuilder) HandleEmotesAndConversions(string[] words)
        {
            string reply = null;
            EmbedBuilder eReply = null;
            for (int i = words.Length - 1; i >= 0; --i)
            {
                string word = words[i];
                bool found = false;
                if (word.StartsWith("#"))
                {
                    string code = word[1..];
                    (found, eReply) = IsWordEmote(code);
                }
                else if (word.StartsWith(":") && word.EndsWith(":") && word.Length > 2)
                {
                    string code = word[1..^1];
                    (found, eReply) = IsWordEmote(code, false);
                }
                if (found)
                    break;

                switch (word)
                {
                    case "C":
                        if (i >= 1)
                        {
                            if (int.TryParse(words[i - 1], out int celsius))
                            {
                                eReply = new EmbedBuilder()
                                {
                                    Description = celsius + " \u00b0C = " + (celsius * 9 / 5 + 32) + " \u00b0F",
                                    Color = new Color(255, 140, 0)
                                };
                            }
                        }
                        break;
                    case "F":
                        if (i >= 1)
                        {
                            if (int.TryParse(words[i - 1], out int fahrenheit))
                            {
                                eReply = new EmbedBuilder()
                                {
                                    Description = fahrenheit + " \u00b0F = " + ((fahrenheit - 32) * 5 / 9) + " \u00b0C",
                                    Color = new Color(255, 140, 0)
                                };
                            }
                        }
                        break;
                }
            }

            return (reply, eReply);
        }


        private (bool, EmbedBuilder) IsWordEmote(string code, bool caseSensitive = true)
        {
            bool emoteComparer(string first, string second) { return caseSensitive ? (first == second) : (first.ToLower() == second.ToLower()); }
            bool found = false;
            int emoteset = -2;
            EmbedBuilder eReply = null;

            foreach (EmoteInfo emote in _emotes)
            {
                if (emote.Code == code)
                {
                    if (emote.EmoteSet == 0 || emote.EmoteSet == 457)
                    {
                        eReply = new EmbedBuilder()
                        {
                            ImageUrl = GetEmoteUrl(emote)
                        };
                        found = true;
                        break;
                    }
                    else if (emote.EmoteSet > emoteset)
                    {
                        eReply = new EmbedBuilder()
                        {
                            ImageUrl = GetEmoteUrl(emote)
                        };
                        found = true;
                        emoteset = emote.EmoteSet;
                    }
                }
            }
            if (!found)
            {
                foreach (EmoteInfo emote in _emotes)
                {
                    if (emoteComparer(code, emote.Code))
                    {
                        if (emote.EmoteSet == 0 || emote.EmoteSet == 457)
                        {
                            eReply = new EmbedBuilder()
                            {
                                ImageUrl = GetEmoteUrl(emote)
                            };
                            found = true;
                            break;
                        }
                        else if (emote.EmoteSet > emoteset)
                        {
                            eReply = new EmbedBuilder()
                            {
                                ImageUrl = GetEmoteUrl(emote)
                            };
                            found = true;
                            emoteset = emote.EmoteSet;
                        }
                    }
                }
            }
            return (found, eReply);
        }

        private string GetEmoteUrl(EmoteInfo emote_info)
        {
            _emotesUsed += 1;
            string reply = "";
            switch (emote_info.Type)
            {
                case EmoteType.Twitch:
                    reply = $"https://static-cdn.jtvnw.net/emoticons/v1/{emote_info.Id}/2.0";
                    break;
                case EmoteType.Bttv:
                    reply = "https:" + _bttvTemplate.Replace("{{id}}", emote_info.Id).Replace("{{image}}", "2x");
                    break;
                case EmoteType.Ffz:
                    reply = $"http://cdn.frankerfacez.com/emoticon/{emote_info.Id}/2";
                    break;
            }

            return reply;
        }

        private async Task<(string, EmbedBuilder)> HandleCommandsAsync(string[] words)
        {
            if (words == null || words.Length < 0)
                return ("An error occurred.", null);

            string reply = null;
            EmbedBuilder eReply = null;
            switch (words[0])
            {
                case "!frozen":
                    if (words.Length >= 2 && words[1] != "pizza")
                        break;
                    // Fall through to frozenpizza
                    goto case "!frozenpizza";
                case "!frozenpizza":
                    _commandsUsed += 1;
                    reply = "*starts making a frozen pizza*";
                    break;
                case "!math":
                    _commandsUsed += 1;
                    if (!string.IsNullOrEmpty(MathQueryURL))
                    {
                        string json = await Program.RequestAsync($"{MathQueryURL}?q={Uri.EscapeDataString(string.Join(' ', words.Skip(1)))}");
                        if (json != null)
                        {
                            Json.MathQuery result = JsonConvert.DeserializeObject<Json.MathQuery>(json);
                            reply = $"({result.Status}): {result.Response}";
                        }
                        else
                        {
                            reply = "There was an error with the request.";
                        }
                    }
                    else
                    {
                        reply = "The bot is misconfigured and missing the math API endpoint. Sorry!";
                    }
                    break;
                case "!bot":
                    _commandsUsed += 1;
                    int users = 0;
                    int channels = 0;
                    long messages;
                    lock (_messageLock)
                    {
                        messages = _messagesReceived;
                    }
                    foreach (SocketGuild server in _client.Guilds)
                    {
                        channels += server.Channels.Count;
                        users += server.MemberCount;
                    }
                    try
                    {
                        var botProcess = System.Diagnostics.Process.GetCurrentProcess();
                        reply = "Source & Information: https://github.com/3ventic/BotVenticCore\nAvailable commands: `!bot` `!frozen pizza` `!foodporn` `!math <query>`";
                        eReply = CreateEmbedWithFields(_embedAuthor, color: Colors.Blue, fields: new string[][]
                        {
                            new string[] { "RAM Usage GC", $"{Math.Ceiling(GC.GetTotalMemory(false) / 1024.0)} KB" },
                            new string[] { "RAM Usage Working Set", $"{Math.Ceiling(botProcess.WorkingSet64 / 1024.0)} KB" },
                            new string[] { "RAM Usage Paged", $"{Math.Ceiling(botProcess.PagedMemorySize64 / 1024.0)} KB" },
                            new string[] { "Uptime", $"{(DateTime.UtcNow - _startedAt).ToString(@"d\ \d\a\y\s\,\ h\ \h\o\u\r\s")}" },
                            new string[] { "Users", users.ToString() },
                            new string[] { "Channels", channels.ToString() },
                            new string[] { "Guilds", _client.Guilds.Count.ToString() },
                            new string[] { "Messages Received", messages.ToString() },
                            new string[] { "Commands Received", _commandsUsed.ToString() },
                            new string[] { "Emotes Used", _emotesUsed.ToString() }
                        });
                    }
                    catch (Exception ex) when (ex is ArgumentNullException || ex is OverflowException || ex is PlatformNotSupportedException)
                    {
                        reply = $"Error: {ex.Message}";
                        Console.WriteLine(ex.ToString());
                    }
                    break;
                case "!foodporn":
                    _commandsUsed += 1;
                    try
                    {
                        var rnd = new Random();
                        int page = rnd.Next(1, 10);
                        string downloadString = await Program.RequestAsync($"http://foodporndaily.com/explore/food/page/{page}/");
                        string regexImgSrc = @"<img[^>]*?src\s*=\s*[""']?([^'"" >]+?)[ '""][^>]*?>";
                        MatchCollection matchesImgSrc = Regex.Matches(downloadString, regexImgSrc, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        int image = rnd.Next(1, matchesImgSrc.Count);
                        reply = matchesImgSrc[image].Groups[1].Value;
                    }
                    catch (Exception ex)
                    {
                        reply = $"Could not get the foodporn image. Error: {ex.Message }";
                        Console.WriteLine(ex.ToString());
                    }
                    break;
                default:
                    break;
            }

            return (reply, eReply);
        }

        private void AddBotReply(IUserMessage bot, SocketMessage user)
        {
            while (_botReplies.Count > _lastHandledMessageOnChannel.Count * 2)
            {
                _botReplies.TryDequeue(out _);
            }
            _botReplies.Enqueue(new IMessage[] { bot, user });
        }

        private enum MessageIndex
        {
            BotReply,
            UserMessage
        }

        private IMessage GetExistingBotReplyOrNull(ulong id)
        {
            foreach (IMessage[] item in _botReplies)
            {
                if (item[(int)MessageIndex.UserMessage].Id == id)
                {
                    return item[(int)MessageIndex.BotReply];
                }
            }
            return null;
        }

        /// <summary>
        /// Update the list of emoticons
        /// </summary>
        private async Task UpdateTwitchEmotesAsync(List<EmoteInfo> e)
        {
            Json.EmoticonImages emotes = JsonConvert.DeserializeObject<Json.EmoticonImages>(await Program.RequestAsync("https://api.twitch.tv/kraken/chat/emoticon_images", true));

            if (emotes == null || emotes.Emotes == null)
            {
                Console.WriteLine("Error loading twitch emotes!");
                return;
            }

            emotes.Emotes.Sort((a, b) =>
            {
                int aSet = 0;
                int bSet = 0;

                if (a != null && a.Set != null)
                    aSet = a.Set ?? 0;
                if (b != null && b.Set != null)
                    bSet = b.Set ?? 0;

                if (aSet == bSet)
                    return 0;

                if (aSet == 0 || aSet == 457)
                    return 1;

                if (bSet == 0 || bSet == 457)
                    return -1;

                return aSet - bSet;
            });

            foreach (Json.Emoticon em in emotes.Emotes)
            {
                e.Add(new EmoteInfo(em.Id, em.Code, EmoteType.Twitch, em.Set ?? 0));
            }
        }

        /// <summary>
        /// Update list of betterttv emoticons
        /// </summary>
        private async Task UpdateBttvEmotesAsync(List<EmoteInfo> e)
        {
            Json.BttvEmoticonImages emotes = JsonConvert.DeserializeObject<Json.BttvEmoticonImages>(await Program.RequestAsync("https://api.betterttv.net/2/emotes"));

            if (emotes == null || emotes.Template == null || emotes.Emotes == null)
            {
                Console.WriteLine("Error loading bttv emotes");
                return;
            }

            _bttvTemplate = emotes.Template;

            foreach (Json.BttvEmoticon em in emotes.Emotes)
            {
                e.Add(new EmoteInfo(em.Id, em.Code, EmoteType.Bttv));
            }
        }


        /// <summary>
        /// Update the list of FrankerFaceZ emoticons
        /// </summary>
        private async Task UpdateFFZEmotesAsync(List<EmoteInfo> e)
        {
            Json.FFZEmoticonSets emotes = JsonConvert.DeserializeObject<Json.FFZEmoticonSets>(await Program.RequestAsync("http://api.frankerfacez.com/v1/set/global"));

            if (emotes == null || emotes.Sets == null || emotes.Sets.Values == null)
            {
                Console.WriteLine("Error loading ffz emotes");
                return;
            }

            foreach (Json.FFZEmoticonImages set in emotes.Sets.Values)
            {
                if (set != null && set.Emotes != null)
                {
                    foreach (Json.FFZEmoticon em in set.Emotes)
                    {
                        e.Add(new EmoteInfo(em.Id, em.Code, EmoteType.Ffz));
                    }
                }
            }
        }

        private EmbedBuilder CreateEmbedWithFields(EmbedAuthorBuilder author = null, string imageUrl = null, Color color = default, params string[][] fields)
        {
            var e = new EmbedBuilder()
            {
                Author = author,
                ImageUrl = imageUrl,
                Color = color
            };
            foreach (string[] fieldcells in fields)
            {
                e.AddField(field =>
                {
                    field.Name = fieldcells[0];
                    field.Value = fieldcells[1];
                    field.IsInline = true;
                });
            }
            return e;
        }

        private bool IsMessageLastRepliedTo(ulong channelId, ulong messageId) => _lastHandledMessageOnChannel.TryGetValue(channelId, out ulong lastMessageId) && lastMessageId == messageId;
        private string NullToEmpty(string str) => str ?? "";
    }
}
