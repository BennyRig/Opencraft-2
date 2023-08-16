using System;
using System.Collections.Generic;
using Opencraft.Deployment;
using Opencraft.Networking;
using Opencraft.Player.Emulated;
using Opencraft.Player.Multiplay;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Scenes;
using UnityEngine;


namespace Opencraft.Bootstrap
{
    /// <summary>
    /// Reads configuration locally or from remote and sets ups deployment or game worlds accordingly
    /// </summary>
    [UnityEngine.Scripting.Preserve]
    public class GameBootstrap : ICustomBootstrap
    {
        private List<World> worlds;

        public bool Initialize(string defaultWorldName)
        {
            // Get the global command line reader class
            CmdArgsReader cmdArgsReader = (CmdArgsReader)GameObject.FindFirstObjectByType(typeof(CmdArgsReader));
            if (!cmdArgsReader.ParseCmdArgs())
            {
                Application.Quit();
                return false;
            }

            worlds = new List<World>();
            
            // Pre world creation initialization
            BootstrappingConfig.BootStrapClass = this;
            NetworkStreamReceiveSystem.DriverConstructor = new NetCodeDriverConstructor();
            
            // Deployment world handles both requesting and answering configuration requests
            if (Config.GetRemoteConfig || Config.isDeploymentService)
                worlds.Add(SetupDeploymentServiceWorld());
            else
            {
                // Use only local configuration
                SetupWorldsFromLocalConfig();
            }

            return true;
        }
    
        /// <summary>
        /// Creates a world with a minimal set of systems necessary for Netcode for Entities to connect, and the
        /// Deployment systems <see cref="DeploymentReceiveSystem"/> and <see cref="DeploymentServiceSystem"/>.
        /// </summary>
        /// <returns></returns>
        private World SetupDeploymentServiceWorld()
        {
            Debug.Log("Creating deployment world");
            // Configure bootstrap with deployment server network endpoints
            NetworkEndpoint.TryParse(Config.DeploymentURL, Config.DeploymentPort, out NetworkEndpoint deploymentEndpoint,
                NetworkFamily.Ipv4);
            BootstrappingConfig.DeploymentClientConnectAddress = deploymentEndpoint;
            BootstrappingConfig.DeploymentPort = Config.DeploymentPort;
            BootstrappingConfig.DeploymentServerListenAddress = NetworkEndpoint.AnyIpv4.WithPort(BootstrappingConfig.DeploymentPort);
            
            // Create the world
            WorldFlagsExtension flags =  Config.GetRemoteConfig ? WorldFlagsExtension.DeploymentClient : WorldFlagsExtension.DeploymentServer;
            var world = new World("DeploymentWorld", (WorldFlags)flags);
            // Fetch all editor and package (but not user-added) systems
            WorldSystemFilterFlags filterFlags =  Config.GetRemoteConfig ? WorldSystemFilterFlags.ClientSimulation : WorldSystemFilterFlags.ServerSimulation;
            NativeList<SystemTypeIndex> systems = TypeManager.GetUnitySystemsTypeIndices(filterFlags);
            
            // Remove built-in NetCode world initialization 
            NativeList<SystemTypeIndex> filteredSystems = new NativeList<SystemTypeIndex>(64, Allocator.Temp);
            foreach (var system in systems)
            {
                var systemName = TypeManager.GetSystemName(system);
                if( systemName.Contains((FixedString64Bytes)"ConfigureThinClientWorldSystem")
                    ||  systemName.Contains((FixedString64Bytes)"ConfigureClientWorldSystem")
                    ||  systemName.Contains((FixedString64Bytes)"ConfigureServerWorldSystem"))
                    continue;
                filteredSystems.Add(system);
            }

            // Add deployment service systems
            if (Config.GetRemoteConfig)
                filteredSystems.Add(TypeManager.GetSystemTypeIndex(typeof(DeploymentReceiveSystem)));
            else
                filteredSystems.Add(TypeManager.GetSystemTypeIndex(typeof(DeploymentServiceSystem)));

            // Add Unity Scene System for managing GUIDs
            filteredSystems.Add(TypeManager.GetSystemTypeIndex(typeof(SceneSystem)));
            // Add NetCode monitor
            filteredSystems.Add(TypeManager.GetSystemTypeIndex(typeof(ConnectionMonitorSystem)));
            
            // Add AuthoringSceneLoader
            filteredSystems.Add(TypeManager.GetSystemTypeIndex(typeof(AuthoringSceneLoaderSystem)));
            
            // Re-sort the systems
            TypeManager.SortSystemTypesInCreationOrder(filteredSystems);
           
            
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, filteredSystems);
            
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);

            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = world;

            return world;
        }

        public void SetBootStrapConfig(string serverURL, ushort serverPort )
        {
            // Configure server network endpoints for server and clients
            NetworkEndpoint.TryParse(serverURL, serverPort, out NetworkEndpoint serverEndpoint,
                NetworkFamily.Ipv4);
            BootstrappingConfig.ClientConnectAddress = serverEndpoint;
            BootstrappingConfig.ServerPort = serverPort;
            BootstrappingConfig.ServerListenAddress = NetworkEndpoint.AnyIpv4.WithPort(serverPort);
        }

        public void SetupWorldsFromLocalConfig()
        {
            SetBootStrapConfig(Config.ServerUrl, Config.ServerPort);
            NativeList<WorldUnmanaged> newWorlds = new NativeList<WorldUnmanaged>(Allocator.Temp);
            SetupWorlds(Config.multiplayStreamingRoles, Config.playTypes, ref newWorlds, Config.NumThinClientPlayers, autoConnect: true);
        }
        
        /// <summary>
        /// Sets up bootstrapping details and creates local worlds
        /// </summary>
        public void SetupWorlds(MultiplayStreamingRoles mRole, BootstrapPlayTypes playTypes, ref NativeList<WorldUnmanaged> newWorlds, int numThinClients, bool autoConnect = true)
        {

            Debug.Log($"Setting up worlds with playType {playTypes} and streaming role {mRole}");
            
            // ================== SETUP WORLDS ==================
            if (mRole == MultiplayStreamingRoles.Host)
            {
                Config.multiplayStreamingRoles = MultiplayStreamingRoles.Host;
            }
            
            //Client
            if (playTypes == BootstrapPlayTypes.Client || playTypes == BootstrapPlayTypes.ClientAndServer)
            {
                if (mRole == MultiplayStreamingRoles.Guest)
                {
                    Config.multiplayStreamingRoles = MultiplayStreamingRoles.Guest;
                    var world = CreateStreamedClientWorld(autoConnect);
                    worlds.Add(world);
                    newWorlds.Add(world.Unmanaged);
                }
                else
                {
                    var world = CreateDefaultClientWorld(autoConnect);
                    worlds.Add(world);
                    newWorlds.Add(world.Unmanaged);
                }
            }

            // Thin client
            if (playTypes == BootstrapPlayTypes.ThinClient && numThinClients > 0)
            {
                var world = CreateThinClientWorlds(numThinClients, autoConnect);
                worlds.AddRange(world);
                foreach (var w in world)
                {
                    newWorlds.Add(w.Unmanaged);
                }
            }

            // Server
            if (playTypes == BootstrapPlayTypes.Server || playTypes == BootstrapPlayTypes.ClientAndServer)
            {
                var world = CreateDefaultServerWorld(autoConnect);
                worlds.Add(world);
                newWorlds.Add(world.Unmanaged);
            }
        }
        

        public static World CreateDefaultClientWorld(bool autoConnect)
        {
            var clientSystems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ClientSimulation |
                                                                         WorldSystemFilterFlags.Presentation);
            // Disable the default NetCode world configuration
            var filteredClientSystems = new List<Type>();
            foreach (var system in clientSystems)
            {
                if(system.Name == "ConfigureThinClientWorldSystem" || system.Name == "ConfigureClientWorldSystem")
                    continue;
                filteredClientSystems.Add(system);
            }
            if(autoConnect)
                filteredClientSystems.Add(typeof(CustomAutoconnectSystem));
            return CreateClientWorld("ClientWorld", WorldFlags.GameClient, filteredClientSystems);
        }

        public static World CreateStreamedClientWorld(bool autoConnect)
        {
            var systems = new List<Type> { typeof(MultiplayInitSystem), typeof(EmulationInitSystem) };
            // TODO Handling autoconnect?
            return CreateClientWorld("StreamingGuestWorld", WorldFlags.Game, systems);
        }
        
        public static List<World> CreateThinClientWorlds(int numThinClients, bool autoConnect)
        {
            List<World> newWorlds = new List<World>();
            
            var thinClientSystems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ThinClientSimulation);
            
            // Disable the default NetCode world configuration
            var filteredThinClientSystems = new List<Type>();
            foreach (var system in thinClientSystems)
            {
                if(system.Name == "ConfigureThinClientWorldSystem" || system.Name == "ConfigureClientWorldSystem")
                    continue;
                filteredThinClientSystems.Add(system);
            }
            
            if(autoConnect)
                filteredThinClientSystems.Add(typeof(CustomAutoconnectSystem));
            
            for (var i = 0; i < numThinClients; i++)
            {
                newWorlds.Add(CreateClientWorld("ThinClientWorld", WorldFlags.GameThinClient, filteredThinClientSystems));
            }

            return newWorlds;
        }

        public static World CreateDefaultServerWorld(bool autoConnect)
        {
            // todo: specify what systems in the server world
            var serverSystems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ServerSimulation);
            
            var filteredServerSystems = new List<Type>();
            foreach (var system in serverSystems)
            {
                if(system.Name == "ConfigureServerWorldSystem")
                    continue;
                filteredServerSystems.Add(system);
            }
            if(autoConnect)
                filteredServerSystems.Add(typeof(CustomAutoconnectSystem));

            return CreateServerWorld("ServerWorld", WorldFlags.GameServer, filteredServerSystems );
        }

        /// <summary>
        /// Utility method for creating new client worlds.
        /// </summary>
        /// <param name="name">The client world name</param>
        /// <param name="flags">WorldFlags for the created world</param>
        /// <param name="systems">List of systems the world will include</param>
        /// <returns></returns>
        public static World CreateClientWorld(string name, WorldFlags flags, IReadOnlyList<Type> systems)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            throw new NotImplementedException();
#else
            var world = new World(name, flags);

            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
            
#if UNITY_DOTSRUNTIME
            CustomDOTSWorlds.AppendWorldToClientTickWorld(world);
#else
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);
#endif

            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = world;

            return world;
#endif
        }
        
        /// <summary>
        /// Utility method for creating a new server world.
        /// Can be used in custom implementations of `Initialize` as well as in your game logic (in particular client/server build)
        /// when you need to create server programmatically (ex: frontend that allow selecting the role or other logic).
        /// </summary>
        /// <param name="name">The server world name</param>
        /// <returns></returns>
        public static World CreateServerWorld(string name, WorldFlags flags, IReadOnlyList<Type> systems)
        {
#if UNITY_CLIENT && !UNITY_SERVER && !UNITY_EDITOR
            throw new NotImplementedException();
#else

            var world = new World(name, flags);
            
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
            

#if UNITY_DOTSRUNTIME
            CustomDOTSWorlds.AppendWorldToServerTickWorld(world);
#else
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);
#endif

            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = world;

            return world;
#endif
        }

        [Serializable]
        public enum BootstrapPlayTypes
        {
            /// <summary>
            /// The application can run as client, server or both. By default, both client and server world are created
            /// and the application can host and play as client at the same time.
            /// <para>
            /// This is the default modality when playing in the editor, unless changed by using the play mode tool.
            /// </para>
            /// </summary>
            ClientAndServer = 0,
            ServerAndClient = 0, // Aliases
            ClientServer    = 0,
            ServerClient    = 0,
            /// <summary>
            /// The application run as a client. Only clients worlds are created and the application should connect to
            /// a server.
            /// </summary>
            Client = 1,
            /// <summary>
            /// The application run as a server. Usually only the server world is created and the application can only
            /// listen for incoming connection.
            /// </summary>
            Server = 2,
            /// <summary>
            /// The application run as a thin client. Only connections to a server and input emulation are performed.
            /// No frontend systems are run. 
            /// </summary>
            StreamedClient=3,
            StreamClient=3,
            GuestClient=3,
            /// <summary>
            /// Minimal client for running player emulation with no frontend, useful for experiments and debugging
            /// </summary>
            ThinClient = 4
        }
    }
    
    
}


