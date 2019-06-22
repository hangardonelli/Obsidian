﻿using Obsidian.BlockData;
using Obsidian.Commands;
using Obsidian.Concurrency;
using Obsidian.Entities;
using Obsidian.Events;
using Obsidian.Logging;
using Obsidian.Net.Packets;
using Obsidian.Net.Packets.Play;
using Obsidian.Plugins;
using Obsidian.Util;
using Obsidian.Util.Registry;
using Obsidian.World;
using Obsidian.World.Generators;
using Qmmands;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Obsidian
{
    public struct QueueChat
    {
        public string Message;
        public byte Position;
    }

    public class Server
    {
        private ConcurrentQueue<QueueChat> _chatmessages;
        private ConcurrentQueue<PlayerDigging> _diggers; // PETALUL this was unintended
        private ConcurrentQueue<PlayerBlockPlacement> _placed;

        private CancellationTokenSource _cts;
        private TcpListener _tcpListener;

        public MinecraftEventHandler Events;
        public PluginManager PluginManager;
        public DateTimeOffset StartTime;

        public OperatorList Operators;

        public List<WorldGenerator> WorldGenerators { get; } = new List<WorldGenerator>();

        public WorldGenerator WorldGenerator { get; private set; }

        public string Path => System.IO.Path.GetFullPath(Id.ToString());

        /// <summary>
        /// Creates a new Server instance. Spawning multiple of these could make a multi-server setup  :thinking:
        /// </summary>
        /// <param name="version">Version the server is running.</param>
        public Server(Config config, string version, int serverId)
        {
            this.Config = config;

            this.Logger = new Logger($"Obsidian ID: {serverId}", Program.Config.LogLevel);

            this.Port = config.Port;
            this.Version = version;
            this.Id = serverId;

            this._tcpListener = new TcpListener(IPAddress.Any, this.Port);

            this.Clients = new ConcurrentHashSet<Client>();

            this._cts = new CancellationTokenSource();
            this._chatmessages = new ConcurrentQueue<QueueChat>();
            this._diggers = new ConcurrentQueue<PlayerDigging>();
            this._placed = new ConcurrentQueue<PlayerBlockPlacement>();
            this.Commands = new CommandService(new CommandServiceConfiguration()
            {
                CaseSensitive = false,
                DefaultRunMode = RunMode.Parallel,
                IgnoreExtraArguments = true
            });
            this.Commands.AddModule<MainCommandModule>();
            this.Events = new MinecraftEventHandler();

            this.PluginManager = new PluginManager(this);
            this.Operators = new OperatorList(this);
        }

        public ConcurrentHashSet<Client> Clients { get; }

        public List<Player> Players
        {
            get
            {
                var list = new List<Player>(Clients.Count);
                foreach (Client client in Clients)
                {
                    if (client.Player == null)
                    {
                        continue;
                    }

                    list.Add(client.Player);
                }
                return list;
            }
        }

        public CommandService Commands { get; }
        public Config Config { get; }
        public Logger Logger { get; }
        public int Id { get; private set; }
        public string Version { get; }
        public int Port { get; }
        public int TotalTicks { get; private set; } = 0;

        private async Task ServerLoop()
        {
            var keepaliveticks = 0;
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(50);

                TotalTicks++;
                await Events.InvokeServerTick();

                keepaliveticks++;
                if (keepaliveticks > 200)
                {
                    var keepaliveid = DateTime.Now.Millisecond;

                    foreach (var clnt in this.Clients.Where(x => x.State == ClientState.Play).ToList())
                        await Task.Factory.StartNew(async () => { await clnt.SendKeepAliveAsync(keepaliveid); });//.ContinueWith(t => { //if (t.IsCompleted) Logger.LogDebugAsync($"Broadcasting keepalive {keepaliveid}"); });
                    keepaliveticks = 0;
                }

                if (_chatmessages.Count > 0)
                {
                    foreach (var clnt in this.Clients.Where(x => x.State == ClientState.Play).ToList())
                        if (_chatmessages.TryDequeue(out QueueChat msg))
                            await Task.Factory.StartNew(async () => { await clnt.SendChatAsync(msg.Message, msg.Position); });
                }

                if (_diggers.Count > 0)
                {
                    if (_diggers.TryDequeue(out PlayerDigging d))
                    {
                        foreach (var clnt in Clients)
                        {
                            var b = new BlockChange(d.Location, BlockRegistry.G(Materials.Air).Id);

                            await clnt.SendBlockChangeAsync(b);
                        }
                    }
                }

                if (_placed.Count > 0)
                {
                    if (_placed.TryDequeue(out PlayerBlockPlacement pbp))
                    {
                        foreach (var clnt in Clients)
                        {
                            var location = pbp.Location;

                            var b = new BlockChange(pbp.Location, BlockRegistry.G(Materials.Cobblestone).Id);
                            await clnt.SendBlockChangeAsync(b);
                        }
                    }
                }

                foreach (var client in Clients)
                {
                    if (client.Timedout)
                        client.Disconnect();
                    if (!client.Tcp.Connected)
                        this.Clients.TryRemove(client);

                    if (Config.Baah.HasValue)
                    {
                        if (client.State == ClientState.Play)
                        {
                            var pos = new Position(client.Player.Transform.X * 8, client.Player.Transform.Y * 8, client.Player.Transform.Z * 8);
                            await client.SendSoundEffectAsync(461, pos, SoundCategory.Master, 1.0f, 1.0f);
                        }
                    }
                }
            }
        }

        public bool CheckPlayerOnline(string username) => this.Clients.Any(x => x.Player != null && x.Player.Username == username);

        public void EnqueueDigging(PlayerDigging d)
        {
            _diggers.Enqueue(d);
        }

        public void EnqueuePlacing(PlayerBlockPlacement pbp)
        {
            _placed.Enqueue(pbp);
        }

        public async Task SendNewPlayer(int id, Guid uuid, Transform position, Player player)
        {
            foreach (var clnt in this.Clients.Where(x => x.State == ClientState.Play).ToList())
            {
                if (clnt.PlayerId == id)
                    continue;

                await clnt.SendEntity(new EntityPacket { Id = id });
                await clnt.SendSpawnMobAsync(id, uuid, 92, position, 1, new Velocity(1, 1, 1), player);
            }
        }

        public async Task SendNewPlayer(int id, string uuid, Transform position, Player player)
        {
            foreach (var clnt in this.Clients.Where(x => x.State == ClientState.Play).ToList())
            {
                if (clnt.PlayerId == id)
                    continue;
                await clnt.SendEntity(new EntityPacket { Id = id });
                await clnt.SendSpawnMobAsync(id, uuid, 92, position, 0, new Velocity(1, 1, 1), player);
            }
        }

        public async Task SendChatAsync(string message, Client source, byte position = 0, bool system = false)
        {
            if (system)
            {
                _chatmessages.Enqueue(new QueueChat() { Message = message, Position = position });
                Logger.LogMessage(message);
            }
            else
            {
                if (!CommandUtilities.HasPrefix(message, '/', out string output))
                {
                    _chatmessages.Enqueue(new QueueChat() { Message = $"<{source.Player.Username}> {message}", Position = position });
                    Logger.LogMessage($"<{source.Player.Username}> {message}");
                    return;
                }

                var context = new CommandContext(source, this);
                IResult result = await Commands.ExecuteAsync(output, context);
                if (!result.IsSuccessful)
                {
                    await context.Client.SendChatAsync($"{ChatColor.Red}Command error: {(result as FailedResult).Reason}", position);
                }
            }
        }

        /// <summary>
        /// Starts this server
        /// </summary>
        /// <returns></returns>
        public async Task StartServer()
        {
            Logger.LogMessage($"Launching Obsidian Server v{Version} with ID {Id}");

            //Check if MPDM and OM are enabled, if so, we can't handle connections
            if (Config.MulitplayerDebugMode && Config.OnlineMode)
            {
                Logger.LogError("Incompatible Config: Multiplayer debug mode can't be enabled at the same time as online mode since usernames will be overwritten");
                StopServer();
                return;
            }

            Logger.LogMessage($"Loading operator list...");
            Operators.Initialize();

            Logger.LogMessage("Registering default entities");
            await RegisterDefaultAsync();

            Logger.LogMessage($"Loading and Initializing plugins...");
            await this.PluginManager.LoadPluginsAsync(this.Logger);

            if (WorldGenerators.FirstOrDefault(g => g.Id == Config.Generator) is WorldGenerator worldGenerator)
            {
                this.WorldGenerator = worldGenerator;
            }
            else
            {
                throw new Exception($"Generator ({Config.Generator}) is unknown.");
            }
            Logger.LogMessage($"World generator set to {this.WorldGenerator.Id} ({this.WorldGenerator.ToString()})");

            Logger.LogDebug($"Set start DateTimeOffset for measuring uptime.");
            this.StartTime = DateTimeOffset.Now;

            Logger.LogMessage("Starting server backend...");
            await Task.Factory.StartNew(async () => { await this.ServerLoop().ConfigureAwait(false); });

            if (!this.Config.OnlineMode)
                this.Logger.LogMessage($"Server is in offline mode..");

            Logger.LogDebug($"Start listening for new clients");
            _tcpListener.Start();

            await BlockRegistry.RegisterAll();

            while (!_cts.IsCancellationRequested)
            {
                var tcp = await _tcpListener.AcceptTcpClientAsync();

                Logger.LogDebug($"New connection from client with IP {tcp.Client.RemoteEndPoint.ToString()}");

                int newplayerid = this.Clients.Count + 1;

                var clnt = new Client(tcp, this.Config, newplayerid, this);
                Clients.Add(clnt);

                await Task.Factory.StartNew(async () => { await clnt.StartConnectionAsync().ConfigureAwait(false); });
            }
            // Cancellation has been requested
            Logger.LogWarning($"Cancellation has been requested. Stopping server...");
            // TODO: TRY TO GRACEFULLY SHUT DOWN THE SERVER WE DONT WANT ERRORS REEEEEEEEEEE
        }

        public void StopServer()
        {
            this.WorldGenerators.Clear(); //Clean up for memory and next boot
            this._cts.Cancel();
        }

        /// <summary>
        /// Registers the "obsidian-vanilla" entities and objects
        /// </summary>
        private async Task RegisterDefaultAsync()
        {
            await RegisterAsync(new SuperflatGenerator());
            await RegisterAsync(new TestBlocksGenerator());
        }

        /// <summary>
        /// Registers a new entity to the server
        /// </summary>
        /// <param name="input">A compatible entry</param>
        /// <exception cref="Exception">Thrown if unknown/unhandable type has been passed</exception>
        public async Task RegisterAsync(params object[] input)
        {
            foreach (object item in input)
            {
                switch (item)
                {
                    default:
                        throw new Exception($"Input ({item.GetType().ToString()}) can't be handled by RegisterAsync.");

                    case WorldGenerator generator:
                        Logger.LogDebug($"Registering {generator.Id}...");
                        WorldGenerators.Add(generator);
                        break;
                }
            }
        }
    }
}