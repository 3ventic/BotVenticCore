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

namespace BotVentic2
{
    class Bot
    {
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private DiscordSocketClient _client;
        private string _token, _clientid;

        private ConcurrentDictionary<ulong, ulong> _lastHandledMessageOnChannel = new ConcurrentDictionary<ulong, ulong>();
        private ConcurrentQueue<IMessage[]> _botReplies = new ConcurrentQueue<IMessage[]>();

        private List<EmoteInfo> _emotes = new List<EmoteInfo>();
        private string _bttvTemplate = "";

        private EmbedAuthorBuilder _embedAuthor = new EmbedAuthorBuilder()
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

            var timer = new Timer(async _ =>
            {
                Console.WriteLine("Updating emotes");
                await UpdateTwitchEmotesAsync(_emotes);
                await UpdateBttvEmotesAsync(_emotes);
                await UpdateFFZEmotesAsync(_emotes);
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

            await _client.LoginAsync(TokenType.Bot, _token);
            await _client.ConnectAsync();
            await Task.Delay(-1);
        }

        internal async Task QuitAsync() => await _client.DisconnectAsync();

        private async Task Client_MessageUpdatedAsync(Optional<SocketMessage> oldMessage, SocketMessage message)
        {
            if (!message.Author.IsBot && message.Channel.Name[0] != '@')
            {
                Console.WriteLine($"[Modify] [{message.Channel.Id}:{message.Channel.Name}] <{message.Author.Id}> {message.Author.Username}: {message.Content}");
                string[] words = message.Content.Split(' ');
                (string reply, Embed eReply) = await HandleCommandsAsync(words);
                if (reply == null && eReply == null)
                {
                    (reply, eReply) = HandleEmotesAndConversions(words);
                }

                if (reply != null || eReply != null)
                {
                    IMessage botMessage = GetExistingBotReplyOrNull(message.Id);
                    if (botMessage == null)
                    {
                        await SendReplyAsync(message, reply, eReply);
                    }
                    else if (botMessage is IUserMessage botMsg)
                    {
                        await botMsg.ModifyAsync((msgProps) =>
                        {
                            msgProps.Content = reply;
                            msgProps.Embed = eReply;
                        });
                    }
                }
            }
        }

        private async Task Client_MessageDeletedAsync(ulong messageId, Optional<SocketMessage> message)
        {
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
                Console.WriteLine($"[Receive] [{message.Channel.Id}:{message.Channel.Name}] <{message.Author.Id}> {message.Author.Username}: {message.Content}");
                if (message.Channel.Name[0] == '@')
                {
                    // Private message
                    await message.Channel.SendMessageAsync($"Add me to a server/guild: {InviteUrl}");
                }
                else
                {
                    string[] words = message.Content.Split(' ');
                    (string reply, Embed eReply) = await HandleCommandsAsync(words);
                    if (reply == null && eReply == null)
                    {
                        (reply, eReply) = HandleEmotesAndConversions(words);
                    }

                    if (reply != null || eReply != null)
                    {
                        await SendReplyAsync(message, reply, eReply);
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

        private (string, Embed) HandleEmotesAndConversions(string[] words)
        {
            string reply = null;
            Embed eReply = null;
            for (int i = words.Length - 1; i >= 0; --i)
            {
                string word = words[i];
                bool found = false;
                if (word.StartsWith("#"))
                {
                    string code = word.Substring(1, word.Length - 1);
                    (found, eReply) = IsWordEmote(code);
                }
                else if (word.StartsWith(":") && word.EndsWith(":") && word.Length > 2)
                {
                    string code = word.Substring(1, word.Length - 2);
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


        private (bool, Embed) IsWordEmote(string code, bool caseSensitive = true)
        {
            Func<string, string, bool> emoteComparer = (first, second) => { return caseSensitive ? (first == second) : (first.ToLower() == second.ToLower()); };
            bool found = false;
            int emoteset = -2;
            Embed eReply = null;

            foreach (var emote in _emotes)
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
                foreach (var emote in _emotes)
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

        private async Task<(string, Embed)> HandleCommandsAsync(string[] words)
        {
            if (words == null || words.Length < 0)
                return ("An error occurred.", null);

            string reply = null;
            Embed eReply = null;
            switch (words[0])
            {
                case "!stream":
                    if (words.Length > 1)
                    {
                        string json = await Program.RequestAsync("https://api.twitch.tv/kraken/streams/" + words[1].ToLower() + "?stream_type=all", true);
                        if (json != null)
                        {
                            var streams = JsonConvert.DeserializeObject<Json.Streams>(json);
                            if (streams != null)
                            {
                                if (streams.Stream == null)
                                {
                                    eReply = new EmbedBuilder()
                                    {
                                        Title = Regex.Split(words[1], @"\W")[0],
                                        Description = "*Offline*",
                                        Color = Colors.Red
                                    };
                                }
                                else
                                {
                                    long ticks = DateTime.UtcNow.Ticks - streams.Stream.CreatedAt.Ticks;
                                    TimeSpan ts = new TimeSpan(ticks);
                                    string name = streams.Stream.Channel.DisplayName ?? words[1];
                                    eReply = CreateEmbedWithFields(new EmbedAuthorBuilder() { IconUrl = streams.Stream.Channel.Logo, Name = name, Url = streams.Stream.Channel.Url }, fields: new string[][] {
                                        new string[] { "Name", name },
                                        new string[] { "Partner", streams.Stream.Channel.IsPartner ? ":white_check_mark:" : ":white_large_square:" },
                                        new string[] { "Title", streams.Stream.Channel.Status ?? "" },
                                        new string[] { "Game", streams.Stream.Game ?? "" },
                                        new string[] { "Viewers", streams.Stream.Viewers.ToString() },
                                        new string[] { "Quality", streams.Stream.VideoHeight + "p" + Math.Round(streams.Stream.FramesPerSecond) },
                                        new string[] { "Uptime", ts.ToString(@"d' day" + (ts.Days == 1 ? "" : "s") + @" 'hh\:mm\:ss") }
                                    });
                                }
                            }
                        }
                    }
                    else
                    {
                        reply = "**Usage:** !stream channel";
                    }
                    break;
                case "!channel":
                    if (words.Length > 1)
                    {
                        string json = await Program.RequestAsync("https://api.twitch.tv/kraken/channels/" + words[1].ToLower(), true);
                        if (json != null)
                        {
                            var channel = JsonConvert.DeserializeObject<Json.Channel>(json);
                            if (channel != null && channel.DisplayName != null)
                            {
                                var registeredFor = new TimeSpan(DateTime.UtcNow.Ticks - channel.Registered.Ticks);
                                int years = (int)registeredFor.TotalDays / 365;
                                int days = (int)registeredFor.TotalDays % 365;
                                eReply = CreateEmbedWithFields(new EmbedAuthorBuilder() { IconUrl = channel.Logo, Name = channel.DisplayName ?? words[1], Url = channel.Url }, color: Colors.Blue, fields: new string[][]
                                {
                                    new string[] { "Partner", channel.IsPartner ? ":white_check_mark:" : ":white_large_square:" },
                                    new string[] { "Title", channel.Status ?? "" },
                                    new string[] { "Registered", $"{channel.Registered:yyyy-MM-dd HH:mm} UTC; {(years > 0 ? years + " years, " : "")}{days} days ago" },
                                    new string[] { "Followers", channel.Followers.ToString() }
                                });
                            }
                        }
                    }
                    else
                    {
                        reply = "**Usage:** !channel channel";
                    }
                    break;
                case "!source":
                    reply = "https://github.com/3ventic/BotVentic";
                    break;
                case "!frozen":
                    if (words.Length >= 2 && words[1] != "pizza")
                        break;
                    // Fall through to frozenpizza
                    goto case "!frozenpizza";
                case "!frozenpizza":
                    reply = "*starts making a frozen pizza*";
                    break;
                case "!bot":
                    int users = 0;
                    foreach (var server in _client.Guilds)
                    {
                        users += server.MemberCount;
                    }
                    try
                    {
                        var botProcess = System.Diagnostics.Process.GetCurrentProcess();
                        reply = "Available commands: `!bot` `!frozen pizza` `!foodporn` `!source` `!stream <Twitch channel name>` `!channel <Twitch channel name>`";
                        eReply = CreateEmbedWithFields(_embedAuthor, color: Colors.Blue, fields: new string[][]
                        {
                            new string[] { "RAM Usage GC", $"{Math.Ceiling(GC.GetTotalMemory(false) / 1024.0)} KB" },
                            new string[] { "RAM Usage Working Set", $"{Math.Ceiling(botProcess.WorkingSet64 / 1024.0)} KB" },
                            new string[] { "RAM Usage Paged", $"{Math.Ceiling(botProcess.PagedMemorySize64 / 1024.0)} KB" },
                            new string[] { "Uptime", $"{(DateTime.UtcNow - _startedAt).ToString(@"d\ \d\a\y\s\,\ h\ \h\o\u\r\s")}" },
                            new string[] { "Users", users.ToString() },
                            new string[] { "Guilds", _client.Guilds.Count.ToString() }
                        });
                    }
                    catch (Exception ex) when (ex is ArgumentNullException || ex is OverflowException || ex is PlatformNotSupportedException)
                    {
                        reply = $"Error: {ex.Message}";
                        Console.WriteLine(ex.ToString());
                    }
                    break;
                case "!foodporn":
                    try
                    {
                        var rnd = new Random();
                        int page = rnd.Next(1, 10);
                        string downloadString = await Program.RequestAsync($"http://foodporndaily.com/explore/food/page/{page}/");
                        string regexImgSrc = @"<img[^>]*?src\s*=\s*[""']?([^'"" >]+?)[ '""][^>]*?>";
                        var matchesImgSrc = Regex.Matches(downloadString, regexImgSrc, RegexOptions.IgnoreCase | RegexOptions.Singleline);
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
                _botReplies.TryDequeue(out IMessage[] throwaway);
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
            foreach (var item in _botReplies)
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
            var emotes = JsonConvert.DeserializeObject<Json.EmoticonImages>(await Program.RequestAsync("https://api.twitch.tv/kraken/chat/emoticon_images", true));

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

            foreach (var em in emotes.Emotes)
            {
                e.Add(new EmoteInfo(em.Id, em.Code, EmoteType.Twitch, em.Set ?? 0));
            }
        }

        /// <summary>
        /// Update list of betterttv emoticons
        /// </summary>
        private async Task UpdateBttvEmotesAsync(List<EmoteInfo> e)
        {
            var emotes = JsonConvert.DeserializeObject<Json.BttvEmoticonImages>(await Program.RequestAsync("https://api.betterttv.net/2/emotes"));

            if (emotes == null || emotes.Template == null || emotes.Emotes == null)
            {
                Console.WriteLine("Error loading bttv emotes");
                return;
            }

            _bttvTemplate = emotes.Template;

            foreach (var em in emotes.Emotes)
            {
                e.Add(new EmoteInfo(em.Id, em.Code, EmoteType.Bttv));
            }
        }


        /// <summary>
        /// Update the list of FrankerFaceZ emoticons
        /// </summary>
        private async Task UpdateFFZEmotesAsync(List<EmoteInfo> e)
        {
            var emotes = JsonConvert.DeserializeObject<Json.FFZEmoticonSets>(await Program.RequestAsync("http://api.frankerfacez.com/v1/set/global"));

            if (emotes == null || emotes.Sets == null || emotes.Sets.Values == null)
            {
                Console.WriteLine("Error loading ffz emotes");
                return;
            }

            foreach (Json.FFZEmoticonImages set in emotes.Sets.Values)
            {
                if (set != null && set.Emotes != null)
                {
                    foreach (var em in set.Emotes)
                    {
                        e.Add(new EmoteInfo(em.Id, em.Code, EmoteType.Ffz));
                    }
                }
            }
        }

        private Embed CreateEmbedWithFields(EmbedAuthorBuilder author = null, string imageUrl = null, Color color = default(Color), params string[][] fields)
        {
            var e = new EmbedBuilder()
            {
                Author = author,
                ImageUrl = imageUrl,
                Color = color
            };
            foreach (var fieldcells in fields)
            {
                e.AddField(field =>
                {
                    field.Name = fieldcells[0];
                    field.Value = fieldcells[1];
                    field.IsInline = true;
                });
            }
            return e.Build();
        }

        private bool IsMessageLastRepliedTo(ulong channelId, ulong messageId) => _lastHandledMessageOnChannel.TryGetValue(channelId, out ulong lastMessageId) && lastMessageId == messageId;
        private string NullToEmpty(string str) => str ?? "";
    }
}
