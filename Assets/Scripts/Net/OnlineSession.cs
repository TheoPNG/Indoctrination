using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace Indoctrination.Net
{
    /// <summary>
    /// Playing with people who are not on your network.
    ///
    /// Two services, doing two jobs that are easy to confuse:
    ///
    /// **Relay** carries the actual game traffic. The host asks Relay for an
    /// allocation and gets a join code back; everyone who enters that code is
    /// routed to the same allocation. Nobody connects to anybody's address, so
    /// there is no port to forward and no IP to hand out - which is the whole
    /// reason for it, because home routers do not accept incoming connections
    /// and asking a playtester to reconfigure theirs is asking them not to
    /// playtest.
    ///
    /// **Lobby** is only a noticeboard. It holds a list of open games and, on
    /// each entry, the Relay join code. Browsing games reads that list; joining
    /// from it just pulls out the code and does exactly what typing the code
    /// does. Nothing about the game itself goes through Lobby.
    ///
    /// The rules and the server are entirely unchanged by any of this. The game
    /// is still one authoritative host and a set of clients speaking Netcode;
    /// this only changes how the pipe between them is built.
    /// </summary>
    public sealed class OnlineSession : MonoBehaviour
    {
        /// <summary>
        /// How often the host tells Lobby its game is still open. Lobby drops an
        /// entry after 30 seconds of silence, so this has to be comfortably
        /// inside that or games vanish from the list while they are being read.
        /// </summary>
        private const float HeartbeatSeconds = 15f;

        /// <summary>Key the Relay join code is filed under on the lobby entry.</summary>
        private const string JoinCodeKey = "joinCode";

        /// <summary>
        /// Encrypted UDP. The alternative, "udp", is unencrypted and only worth
        /// having if DTLS is unavailable on a platform, which it is not here.
        /// </summary>
        private const string ConnectionType = "dtls";

        public static OnlineSession Instance { get; private set; }

        /// <summary>One open game, as the browser lists it.</summary>
        public readonly struct Listing
        {
            public Listing(string id, string name, int players, int maxPlayers, string joinCode)
            {
                Id = id;
                Name = name;
                Players = players;
                MaxPlayers = maxPlayers;
                JoinCode = joinCode;
            }

            public string Id { get; }
            public string Name { get; }
            public int Players { get; }
            public int MaxPlayers { get; }
            public string JoinCode { get; }
        }

        /// <summary>The code to pass round, once hosting. Empty when not hosting online.</summary>
        public string JoinCode { get; private set; } = "";

        /// <summary>What went wrong, for the board to show. Empty when nothing did.</summary>
        public string LastError { get; private set; } = "";

        /// <summary>True while something is being asked of the services.</summary>
        public bool Busy { get; private set; }

        /// <summary>Whether the services are up and this machine is signed in.</summary>
        public bool Ready { get; private set; }

        private Lobby _hosted;
        private float _nextHeartbeat;

        public static OnlineSession CreateOn(Transform parent)
        {
            if (Instance != null)
            {
                return Instance;
            }

            var go = new GameObject("Online Session");
            go.transform.SetParent(parent, false);
            Instance = go.AddComponent<OnlineSession>();
            return Instance;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Starts the services and signs this machine in anonymously.
        ///
        /// Anonymous on purpose: an account is a thing to create, remember and
        /// lose, and the game has never asked for one. The identity lasts as
        /// long as the install and is only used so the services know who is
        /// holding an allocation.
        /// </summary>
        public async Task<bool> ConnectToServicesAsync()
        {
            if (Ready)
            {
                return true;
            }

            try
            {
                Busy = true;
                LastError = "";

                if (UnityServices.State != ServicesInitializationState.Initialized)
                {
                    await UnityServices.InitializeAsync();
                }

                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                }

                Ready = true;
                return true;
            }
            catch (Exception exception)
            {
                // Almost always one of two things, and the message says which:
                // the project is not linked to a Unity cloud project, or Relay
                // and Lobby have not been switched on for it.
                LastError = $"Could not reach the online services: {exception.Message}";
                Debug.LogWarning(LastError);
                return false;
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>
        /// Opens a game: an allocation to play through, a join code for it, and
        /// a lobby entry so it can be found without the code being passed round
        /// by hand.
        ///
        /// The host is started **before** the lobby is published. A game listed
        /// before it can accept anybody is a game that fails to join for
        /// whoever is quickest off the mark.
        /// </summary>
        public async Task<bool> HostAsync(string gameName, int maxPlayers)
        {
            if (!await ConnectToServicesAsync())
            {
                return false;
            }

            try
            {
                Busy = true;
                LastError = "";

                // Connections, not players: the host is not connecting to
                // itself, so a four-player game needs three.
                var allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers - 1);
                JoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

                var endpoint = EndpointOf(allocation.ServerEndpoints);
                Transport().SetRelayServerData(new RelayServerData(
                    endpoint.Host,
                    (ushort)endpoint.Port,
                    allocation.AllocationIdBytes,
                    allocation.ConnectionData,
                    allocation.ConnectionData,
                    allocation.Key,
                    endpoint.Secure));

                if (!NetworkManager.Singleton.StartHost())
                {
                    LastError = "The host would not start.";
                    return false;
                }

                _hosted = await LobbyService.Instance.CreateLobbyAsync(
                    string.IsNullOrWhiteSpace(gameName) ? "Indoctrination" : gameName,
                    maxPlayers,
                    new CreateLobbyOptions
                    {
                        IsPrivate = false,
                        Data = new Dictionary<string, DataObject>
                        {
                            [JoinCodeKey] = new(DataObject.VisibilityOptions.Public, JoinCode)
                        }
                    });

                _nextHeartbeat = Time.realtimeSinceStartup + HeartbeatSeconds;
                return true;
            }
            catch (Exception exception)
            {
                LastError = $"Could not open the game: {exception.Message}";
                Debug.LogWarning(LastError);
                return false;
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>
        /// Joins whatever that code points at. This is the only way in - the
        /// browser hands its selected game's code to exactly this.
        /// </summary>
        public async Task<bool> JoinAsync(string joinCode)
        {
            if (string.IsNullOrWhiteSpace(joinCode))
            {
                LastError = "Enter the code the host gave you.";
                return false;
            }

            if (!await ConnectToServicesAsync())
            {
                return false;
            }

            try
            {
                Busy = true;
                LastError = "";

                var join = await RelayService.Instance.JoinAllocationAsync(joinCode.Trim().ToUpperInvariant());
                var endpoint = EndpointOf(join.ServerEndpoints);

                Transport().SetRelayServerData(new RelayServerData(
                    endpoint.Host,
                    (ushort)endpoint.Port,
                    join.AllocationIdBytes,
                    join.ConnectionData,
                    join.HostConnectionData,
                    join.Key,
                    endpoint.Secure));

                if (!NetworkManager.Singleton.StartClient())
                {
                    LastError = "Could not start connecting.";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                LastError = $"Could not join that game: {exception.Message}";
                Debug.LogWarning(LastError);
                return false;
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>
        /// Every open game anybody has published. Entries without a join code
        /// are skipped - there is no way to reach one, so listing it only offers
        /// a button that cannot work.
        /// </summary>
        public async Task<List<Listing>> BrowseAsync()
        {
            if (!await ConnectToServicesAsync())
            {
                return new List<Listing>();
            }

            try
            {
                Busy = true;
                LastError = "";

                var found = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
                {
                    Count = 25,
                    Filters = new List<QueryFilter>
                    {
                        new(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT)
                    }
                });

                return found.Results
                    .Select(lobby => new Listing(
                        lobby.Id,
                        lobby.Name,
                        lobby.MaxPlayers - lobby.AvailableSlots,
                        lobby.MaxPlayers,
                        lobby.Data != null && lobby.Data.TryGetValue(JoinCodeKey, out var code)
                            ? code.Value
                            : ""))
                    .Where(listing => !string.IsNullOrEmpty(listing.JoinCode))
                    .ToList();
            }
            catch (Exception exception)
            {
                LastError = $"Could not read the game list: {exception.Message}";
                Debug.LogWarning(LastError);
                return new List<Listing>();
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>
        /// Takes a hosted game off the list. Called when the host leaves; without
        /// it the entry lingers until Lobby times it out, and until then the
        /// browser advertises a game nobody can join.
        /// </summary>
        public async void CloseAsync()
        {
            JoinCode = "";

            var lobby = _hosted;
            _hosted = null;

            if (lobby == null)
            {
                return;
            }

            try
            {
                await LobbyService.Instance.DeleteLobbyAsync(lobby.Id);
            }
            catch (Exception exception)
            {
                // Nothing to do about it and nothing depends on it - the entry
                // times out by itself. Worth a line in the log, not a failure.
                Debug.LogWarning($"Could not remove the lobby entry: {exception.Message}");
            }
        }

        private void Update()
        {
            if (_hosted == null || Time.realtimeSinceStartup < _nextHeartbeat)
            {
                return;
            }

            _nextHeartbeat = Time.realtimeSinceStartup + HeartbeatSeconds;
            Heartbeat(_hosted.Id);
        }

        private static async void Heartbeat(string lobbyId)
        {
            try
            {
                await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Lobby heartbeat failed: {exception.Message}");
            }
        }

        /// <summary>
        /// The encrypted endpoint of an allocation, or whatever it offers if
        /// there is no encrypted one.
        /// </summary>
        private static RelayServerEndpoint EndpointOf(List<RelayServerEndpoint> endpoints)
        {
            return endpoints.FirstOrDefault(e => e.ConnectionType == ConnectionType)
                   ?? endpoints[0];
        }

        private static UnityTransport Transport()
        {
            return NetworkManager.Singleton.GetComponent<UnityTransport>();
        }
    }
}
