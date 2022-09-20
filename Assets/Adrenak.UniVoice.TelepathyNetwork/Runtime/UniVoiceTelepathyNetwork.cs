using System.Collections.Generic;

using Telepathy;
using System;
using System.Runtime.Serialization.Formatters.Binary;
using System.IO;
using UnityEngine;
using System.Linq;
using Adrenak.BRW;

// TODO: Code is a mess, refactor and document later.
namespace Adrenak.UniVoice.TelepathyNetwork {
    public class UniVoiceTelepathyNetwork : MonoBehaviour, IChatroomNetwork {
        const string NEW_CLIENT_INIT = "NEW_CLIENT_INIT";
        const string CLIENT_JOINED = "CLIENT_JOINED";
        const string CLIENT_LEFT = "CLIENT_LEFT";
        const string AUDIO_SEGMENT = "AUDIO_SEGMENT";

        // HOSTING EVENTS
        public event Action OnCreatedChatroom;
        public event Action<Exception> OnChatroomCreationFailed;
        public event Action OnClosedChatroom;

        // JOINING EVENTS
        public event Action<short> OnJoinedChatroom;
        public event Action<Exception> OnChatroomJoinFailed;
        public event Action OnLeftChatroom;

        // PEER EVENTS
        public event Action<short> OnPeerJoinedChatroom;
        public event Action<short> OnPeerLeftChatroom;

        // AUDIO EVENTS
        public event Action<short, ChatroomAudioSegment> OnAudioReceived;
        public event Action<short, ChatroomAudioSegment> OnAudioSent;

        public short OwnID { get; private set; } = -1;

        public List<short> PeerIDs { get; private set; } = new List<short>();

        Server server;
        Client client;
        int port;

        [Obsolete("Use UniVoiceTelepathyNetwork.New static method instead of new keyword", true)]
        public UniVoiceTelepathyNetwork() { }

        public static UniVoiceTelepathyNetwork New(int port) {
            var go = new GameObject("UniVoiceTelepathyNetwork");
            var cted = go.AddComponent<UniVoiceTelepathyNetwork>();
            DontDestroyOnLoad(go);
            cted.port = port;
            cted.server = new Server(32 * 1024);
            cted.server.OnConnected += cted.OnConnected_Server;
            cted.server.OnDisconnected += cted.OnDisconnected_Server;
            cted.server.OnData += cted.OnData_Server;

            cted.client = new Client(32 * 1024);
            cted.client.OnData += cted.OnData_Client;
            cted.client.OnDisconnected += cted.OnDisconnected_Client;
            return cted;
        }

        void Update() {
            server?.Tick(100);
            client?.Tick(100);
        }

        void OnDestroy() {
            Dispose();
        }

        public void Dispose() {
            client.Disconnect();
            server.Stop();
        }

        void OnData_Client(ArraySegment<byte> data) {
            try {
                var packet = new BytesReader(data.Array);
                var tag = packet.ReadString();
                
                switch (tag) {
                    case NEW_CLIENT_INIT:
                        OwnID = (short)packet.ReadInt();
                        OnJoinedChatroom?.Invoke(OwnID);
                        PeerIDs = packet.ReadIntArray().Select(x => (short)x).ToList();
                        foreach (var peer in PeerIDs)
                            OnPeerJoinedChatroom?.Invoke(peer);
                        break;
                    case CLIENT_JOINED:
                        var joinedID = (short)packet.ReadInt();
                        if (!PeerIDs.Contains(joinedID))
                            PeerIDs.Add(joinedID);
                        OnPeerJoinedChatroom?.Invoke(joinedID);
                        break;
                    case CLIENT_LEFT:
                        var leftID = (short)packet.ReadInt();
                        if (PeerIDs.Contains(leftID))
                            PeerIDs.Remove(leftID);
                        OnPeerLeftChatroom?.Invoke(leftID);
                        break;
                    case AUDIO_SEGMENT:
                        var sender = packet.ReadShort();
                        var recepient = packet.ReadShort();
                        if(recepient == OwnID) {
                            var segment = FromByteArray<ChatroomAudioSegment>(packet.ReadByteArray());
                            OnAudioReceived?.Invoke(sender, segment);
                        }
                        break;
                }
            }
            catch { }
        }

        void OnDisconnected_Client() {
            if (OwnID == -1) {
                OnChatroomJoinFailed?.Invoke(new Exception("Could not join chatroom"));
                return;
            }
            PeerIDs.Clear();
            OwnID = -1;
            OnLeftChatroom?.Invoke();
        }

        void OnConnected_Server(int obj) {
            var id = (short)obj;
            if(id != 0) {
                if (!PeerIDs.Contains(id))
                    PeerIDs.Add(id);
                foreach (var peer in PeerIDs) {
                    // Let the new client know its ID
                    if (peer == id) {
                        var peersForNewClient = PeerIDs
                            .Where(x => x != peer)
                            .Select(x => (int)x)
                            .ToList();
                        peersForNewClient.Add(0);

                        var newClientPacket = new BytesWriter()
                            .WriteString(NEW_CLIENT_INIT)
                            .WriteInt(id)
                            .WriteIntArray(peersForNewClient.ToArray());
                        server.Send(peer, new ArraySegment<byte>(newClientPacket.Bytes));
                    }
                    // Let other clients know a new peer has joined
                    else {
                        var oldClientsPacket = new BytesWriter()
                            .WriteString(CLIENT_JOINED)
                            .WriteInt(id);
                        server.Send(peer, new ArraySegment<byte>(oldClientsPacket.Bytes));
                    }
                }
                OnPeerJoinedChatroom?.Invoke(id);
            }
        }

        void OnData_Server(int sender, ArraySegment<byte> data) {
            var packet = new BytesReader(data.Array);
            var tag = packet.ReadString();

            if (tag.Equals(AUDIO_SEGMENT)) {
                var audioSender = packet.ReadShort();
                var recipient = packet.ReadShort();
                var segmentBytes = packet.ReadByteArray();

                if (recipient == OwnID) {
                    var segment = FromByteArray<ChatroomAudioSegment>(segmentBytes);
                    OnAudioReceived?.Invoke(audioSender, segment);
                }
                else if (PeerIDs.Contains(recipient))
                    server.Send(recipient, new ArraySegment<byte>(segmentBytes));
            }
        }

        void OnDisconnected_Server(int id) {
            if(id != 0) {
                if (PeerIDs.Contains((short)id))
                    PeerIDs.Remove((short)id);
                foreach (var peer in PeerIDs) {

                    var packet = new BytesWriter()
                        .WriteString(CLIENT_LEFT)
                        .WriteInt(id);
                    server.Send(peer, new ArraySegment<byte>(packet.Bytes));
                }
                OnPeerLeftChatroom?.Invoke((short)id);
            }
        }

        public void HostChatroom(object data = null) {
            if (!server.Active) {
                if(server.Start(port)) {
                    OwnID = 0;
                    PeerIDs.Clear();
                    OnCreatedChatroom?.Invoke();
                }
            }
            else
                Debug.LogWarning("HostChatroom failed. Already hosting a chatroom. Close and host again.");
        }

        /// <summary>
        /// Closes the chatroom, if hosting
        /// </summary>
        /// <param name="data"></param>
        public void CloseChatroom(object data = null) {
            if (server != null && server.Active) {
                server.Stop();
                PeerIDs.Clear();
                if (OwnID == -1) {
                    OnChatroomCreationFailed?.Invoke(new Exception("Could not create chatroom"));
                    return;
                }
                OwnID = -1;
                OnClosedChatroom?.Invoke();
            }
            else
                Debug.LogWarning("CloseChatroom failed. Not hosting a chatroom currently");
        }

        /// <summary>
        /// Joins a chatroom. If passed null, will connect to "localhost"
        /// </summary>
        /// <param name="data"></param>
        public void JoinChatroom(object data = null) {
            if(client.Connected || client.Connecting)
                client.Disconnect();
            if (client.Connected) {
                Debug.LogWarning("JoinChatroom failed. Already connected to a chatroom. Leave and join again.");
                return;
            }
            if (client.Connecting) {
                Debug.LogWarning("JoinChatroom failed. Currently attempting to connect to a chatroom. Leave and join again");
                return;
            }
            var ip = "localhost";
            if (data != null)
                ip = (string)data;
            client.Connect(ip, port);
        }

        /// <summary>
        /// Leave a chatroom. Data passed is not used
        /// </summary>
        /// <param name="data"></param>
        public void LeaveChatroom(object data = null) {
            if (client.Connected || client.Connecting)
                client.Disconnect();
            else
                Debug.LogWarning("LeaveChatroom failed. Currently connected to any chatroom");
        }

        /// <summary>
        /// Send a <see cref="ChatroomAudioSegment"/> to a peer
        /// This method is used internally by <see cref="ChatroomAgent"/>
        /// invoke it manually at your own risk!
        /// </summary>
        /// <param name="peerID"></param>
        /// <param name="data"></param>
        public void SendAudioSegment(short peerID, ChatroomAudioSegment data) {
            if (!server.Active && !client.Connected) return;

            var packet = new BytesWriter()
                .WriteString(AUDIO_SEGMENT)
                // Sender
                .WriteShort(OwnID)
                // Recipient
                .WriteShort(peerID)
                .WriteByteArray(ToByteArray(data));

            if (server.Active) 
                server.Send(peerID, new ArraySegment<byte>(packet.Bytes));
            else if (client.Connected) 
                client.Send(new ArraySegment<byte>(packet.Bytes));

            OnAudioSent?.Invoke(peerID, data);
        }

        public byte[] ToByteArray<T>(T obj) {
            if (obj == null)
                return null;
            BinaryFormatter bf = new BinaryFormatter();
            using (MemoryStream ms = new MemoryStream()) {
                bf.Serialize(ms, obj);
                return ms.ToArray();
            }
        }

        public T FromByteArray<T>(byte[] data) {
            if (data == null)
                return default;
            BinaryFormatter bf = new BinaryFormatter();
            using (MemoryStream ms = new MemoryStream(data)) {
                object obj = bf.Deserialize(ms);
                return (T)obj;
            }
        }
    }
}
