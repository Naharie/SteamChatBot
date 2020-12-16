using Discord;
using Discord.WebSocket;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;

using SteamKit2;

using Newtonsoft.Json;

namespace SteamChatBot
{
	public class Program
	{
        static DiscordSocketClient discord;
		static SocketGuild server;

		static SteamClient steam;
		static CallbackManager manager;
		static SteamUser user;
		static SteamFriends friends;
		static string authCode;

		static Dictionary<string, ulong> discordChannels = new Dictionary<string, ulong>();
		static Dictionary<ulong, string> _discordChannels = new Dictionary<ulong, string>();
		static Dictionary<string, IDisposable> discordTyping = new Dictionary<string, IDisposable>();

		static Dictionary<string, SteamID> steamChats = new Dictionary<string, SteamID>();
		static Dictionary<SteamID, string> _steamChats = new Dictionary<SteamID, string>();
		static HashSet<SteamID> steamFriends = new HashSet<SteamID>();

		static Dictionary<string, ulong> discordQueue = new Dictionary<string, ulong>();

        public static async Task Main()
		{
			await SetupDiscord();
            await Task.Delay(-1);
		}

		static void LoadDiscordQueue()
		{
			if (File.Exists("discord.json"))
			{
				discordQueue = JsonConvert.DeserializeObject<Dictionary<string, ulong>>(File.ReadAllText("discord.json"));
			}
		}
		static void SaveDiscordQueue() => File.WriteAllText("discord.json", JsonConvert.SerializeObject(discordQueue));

		// Steam

		static void SetupSteam()
		{
			if (steam != null)
			{
				return;
			}

			steam = new SteamClient(SteamConfiguration.Create(config =>
				config
					.WithProtocolTypes (ProtocolTypes.All)
					.WithWebAPIKey("{API KEY HERE}")
			));
			manager = new CallbackManager(steam);

			user = steam.GetHandler<SteamUser>();
			friends = steam.GetHandler<SteamFriends>();

			manager.Subscribe<SteamFriends.FriendsListCallback>(OnGetFriendsList);
			manager.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);

			manager.Subscribe<SteamFriends.ChatInviteCallback>(OnChatInvite);
			
			manager.Subscribe<SteamFriends.FriendMsgCallback>(OnChatMessage);
			manager.Subscribe<SteamFriends.ChatMsgCallback>(OnChatMessage);
			manager.Subscribe<SteamFriends.FriendMsgHistoryCallback>(OnOfflineMessage);

			manager.Subscribe<SteamClient.ConnectedCallback>(OnSteamConnected);
			manager.Subscribe<SteamClient.DisconnectedCallback>(OnSteamDisconnected);

			manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
			manager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);

			steam.Connect();

			ThreadPool.QueueUserWorkItem(_ =>
			{
				while (true)
				{
					manager.RunWaitCallbacks(TimeSpan.FromSeconds(10));
				}
			});
		}

		static void AttemptLogon()
		{
			byte[] sentryHash = null;

			if (File.Exists("sentry.bin"))
			{
				var sentryFile = File.ReadAllBytes("sentry.bin");
				sentryHash = CryptoHelper.SHAHash(sentryFile);
			}

			user.LogOn(new SteamUser.LogOnDetails
			{
				Username = "{USERNAME HERE}",
				Password = "{PASSWORD HERE}",

				AuthCode = authCode,
				SentryFileHash = sentryHash,
			});
		}
		static void OnSteamConnected(SteamClient.ConnectedCallback data)
		{
			AttemptLogon();
		}
		static async void OnSteamDisconnected(SteamClient.DisconnectedCallback data)
		{
			await Task.Delay(60_000);
			steam.Connect();
		}

		static void OnLoggedOn(SteamUser.LoggedOnCallback data)
		{
			var isSteamGuard = data.Result == EResult.AccountLogonDenied;

			if (isSteamGuard)
			{
				Console.WriteLine("This account is SteamGuard protected!");

				Console.Write($"Please enter the auth code sent to the email at {data.EmailDomain}: ");
				authCode = Console.ReadLine();

				AttemptLogon();

				return;
			}

			Console.WriteLine ("Logged on to Steam.");
			friends.SetPersonaState(EPersonaState.Online);
		}
		static void OnMachineAuth(SteamUser.UpdateMachineAuthCallback data)
		{
			Console.WriteLine("Updating sentryfile...");

			int fileSize;
			byte[] sentryHash;

			using (var fs = File.Open("sentry.bin", FileMode.OpenOrCreate, FileAccess.ReadWrite))
			{
				fs.Seek(data.Offset, SeekOrigin.Begin);
				fs.Write(data.Data, 0, data.BytesToWrite);
				fileSize = (int)fs.Length;

				fs.Seek(0, SeekOrigin.Begin);
				using (var sha = SHA1.Create())
				{
					sentryHash = sha.ComputeHash(fs);
				}
			}

			user.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
			{
				JobID = data.JobID,

				FileName = data.FileName,

				BytesWritten = data.BytesToWrite,
				FileSize = fileSize,
				Offset = data.Offset,

				Result = EResult.OK,
				LastError = 0,

				OneTimePassword = data.OneTimePassword,
				SentryFileHash = sentryHash,
			});

			Console.WriteLine("Done!");
		}

		static void OnGetFriendsList(SteamFriends.FriendsListCallback data) => friends.RequestOfflineMessages();
		static void OnPersonaState(SteamFriends.PersonaStateCallback data)
		{
			if (data.FriendID == user.SteamID)
			{
				return;
			}

			var friend = data.FriendID;
			var name = data.Name.ToLower();

			steamChats[name] = friend;
			_steamChats[friend] = name;

			steamFriends.Add(friend);
			EnsureDiscordChannel(name);
		}

		// Automatically join any group chats.
		static void OnChatInvite(SteamFriends.ChatInviteCallback data)
		{
			friends.JoinChat(data.ChatRoomID);
			EnsureDiscordChannel(data.ChatRoomName);
		}

		static async Task HandleMessage(SteamID sender, string message)
		{
			if (_steamChats.TryGetValue (sender, out var name))
			{
				if (discordChannels.TryGetValue (name, out var id))
				{
					// Remove the typing prompt.
					if (discordTyping.TryGetValue (name, out var trigger))
					{
						trigger.Dispose();
					}

					await (server.GetChannel(id) as SocketTextChannel).SendMessageAsync(message);
				}
			}
		}
		static void ShowTyping(SteamID sender)
		{
			if (_steamChats.TryGetValue(sender, out var name))
			{
				if (discordChannels.TryGetValue(name, out var id))
				{
					var copy = (server.GetChannel(id) as SocketTextChannel).EnterTypingState();
					discordTyping[name] = copy;

					Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
					{
					   try
					   {
						   copy.Dispose();
					   }
					   catch
					   {
					   }
				    });
				}
			}
		}

		static async void OnChatMessage(SteamFriends.FriendMsgCallback data)
		{
			if (data.Sender == user.SteamID) return;

			if (!string.IsNullOrWhiteSpace(data.Message))
			{
				await HandleMessage(data.Sender, data.Message);
			}
			else if (data.EntryType == EChatEntryType.Typing)
			{
				ShowTyping(data.Sender);
			}
			else
			{
				await HandleMessage(data.Sender, "Unknown message: " + data.EntryType.ToString());
			}
		}
		static async void OnChatMessage(SteamFriends.ChatMsgCallback data)
		{
			if (data.ChatterID == user.SteamID) return;

			if (!string.IsNullOrWhiteSpace(data.Message))
			{
				await HandleMessage(data.ChatRoomID, data.Message);
			}
			else if (data.ChatMsgType == EChatEntryType.Typing)
			{
				ShowTyping(data.ChatRoomID);
			}
			else
			{
				await HandleMessage(data.ChatterID, "Unknown message: " + data.ChatMsgType.ToString());
			}
		}

		static async void OnOfflineMessage(SteamFriends.FriendMsgHistoryCallback data)
		{
			foreach (var message in data.Messages)
			{
				await HandleMessage(data.SteamID, message.Message);
			}
		}

		// Discord

		static async Task SetupDiscord()
		{
			discord = new DiscordSocketClient();
			discord.Log += Log;

			await discord.LoginAsync(TokenType.Bot, "{BOT TOKEN HERE}");
			await discord.StartAsync();

			discord.Ready += async () =>
			{
				LoadDiscordQueue();
				server = discord.Guilds.First();

				foreach (var channel in server.Channels)
				{
					if (channel is SocketTextChannel text)
					{
						discordChannels[channel.Name] = channel.Id;
						_discordChannels[channel.Id] = channel.Name;

						if (discordQueue.TryGetValue (channel.Name, out var lastMessage))
						{
							await foreach (var group in text.GetMessagesAsync(lastMessage, Direction.After, Int32.MaxValue))
							{
								foreach (var message in group)
								{
									SendSteamMessage(message.Channel.Name, message.Content, message.Id);
								}

								SaveDiscordQueue();
							}
						}
					}
				}

				SetupSteam();
			};
			discord.Disconnected += async (error) =>
			{
				await Task.Delay(60_000);
				await discord.LoginAsync(TokenType.Bot, "{BOT TOKEN HERE}");
				await discord.StartAsync();
			};

			discord.UserIsTyping += UserTyping;

			discord.MessageReceived += MessageReceived;
			discord.MessageUpdated += MessageUpdated;
		}

		static Task EnsureDiscordChannel(string name)
		{
			name = name.ToLower();

			if (discordChannels.ContainsKey (name) || server.Channels.Any(channel => channel.Name == name))
			{
				return Task.CompletedTask;
			}

			return server.CreateTextChannelAsync(name)
				.ContinueWith(channel =>
				{
					discordChannels[name] = channel.Result.Id;
					_discordChannels[channel.Result.Id] = name;
				});
		}

		static void SendSteamMessage(string channel, string message, ulong messageId)
		{
			if (steamChats.TryGetValue(channel, out var id))
			{
				if (steamFriends.Contains(id))
				{
					friends.SendChatMessage(id, EChatEntryType.ChatMsg, message ?? "-");
				}
				else
				{
					friends.SendChatRoomMessage(id, EChatEntryType.ChatMsg, message ?? "-");
				}
			}

			discordQueue[channel] = messageId;
		}

		static Task UserTyping(SocketUser user, ISocketMessageChannel channel)
		{
			if (steamChats.TryGetValue(channel.Name, out var id))
			{
				if (steamFriends.Contains(id))
				{
					friends.SendChatMessage(id, EChatEntryType.Typing, "");
				}
				else
				{
					friends.SendChatRoomMessage(id, EChatEntryType.Typing, "");
				}
			}

			return Task.CompletedTask;
		}

		static Task MessageReceived(SocketMessage message)
		{
			if (message.Source == MessageSource.Bot)
			{
				return Task.CompletedTask;
			}

			SendSteamMessage(message.Channel.Name, message.Content, message.Id);
			SaveDiscordQueue();
			return Task.CompletedTask;
		}
		static Task MessageUpdated(Cacheable<IMessage, ulong> cache, SocketMessage message, ISocketMessageChannel channel)
		{
			if (message.Source == MessageSource.Bot)
			{
				return Task.CompletedTask;
			}

			SendSteamMessage(message.Channel.Name, message.Content, message.Id);
			SaveDiscordQueue();
			return Task.CompletedTask;
		}

		static Task Log(LogMessage message)
		{
			Console.WriteLine(message.ToString());
			return Task.CompletedTask;
		}
	}
}