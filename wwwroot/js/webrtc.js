// WebRTC P2P Communication using PeerJS

let peer = null;
let connection = null;
let dotNetRef = null;
let myPeerId = null;

window.WebRtc = {
    initialize: function (dotNetReference) {
        dotNetRef = dotNetReference;
        console.log('WebRTC (PeerJS) initialized');
    },

    // ルームを作成（先手）- 短いルームIDを返す
    createRoom: async function () {
        return new Promise((resolve, reject) => {
            // 6文字のランダムなルームIDを生成
            const roomId = generateRoomId();

            peer = new Peer(roomId, {
                debug: 2
            });

            peer.on('open', (id) => {
                console.log('Room created with ID:', id);
                myPeerId = id;

                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnConnectionStateChanged', 'connecting');
                }

                resolve(id);
            });

            peer.on('connection', (conn) => {
                console.log('Peer connected:', conn.peer);
                connection = conn;
                setupConnectionHandlers();
            });

            peer.on('error', (err) => {
                console.error('Peer error:', err);
                if (err.type === 'unavailable-id') {
                    // IDが既に使用されている場合は再試行
                    peer.destroy();
                    resolve(window.WebRtc.createRoom());
                } else {
                    reject(err);
                }
            });

            peer.on('disconnected', () => {
                console.log('Peer disconnected');
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnConnectionStateChanged', 'disconnected');
                }
            });
        });
    },

    // ルームに参加（後手）
    joinRoom: async function (roomId) {
        return new Promise((resolve, reject) => {
            peer = new Peer({
                debug: 2
            });

            peer.on('open', (id) => {
                console.log('My peer ID:', id);
                myPeerId = id;

                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnConnectionStateChanged', 'connecting');
                }

                // ルームに接続
                connection = peer.connect(roomId, {
                    reliable: true
                });

                setupConnectionHandlers();
                resolve(true);
            });

            peer.on('error', (err) => {
                console.error('Peer error:', err);
                reject(err);
            });

            peer.on('disconnected', () => {
                console.log('Peer disconnected');
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnConnectionStateChanged', 'disconnected');
                }
            });
        });
    },

    sendMessage: function (message) {
        if (connection && connection.open) {
            connection.send(message);
            console.log('Message sent:', message);
            return true;
        }
        console.warn('Connection not ready');
        return false;
    },

    getConnectionState: function () {
        if (!connection) return 'disconnected';
        return connection.open ? 'connected' : 'connecting';
    },

    disconnect: function () {
        if (connection) {
            connection.close();
            connection = null;
        }
        if (peer) {
            peer.destroy();
            peer = null;
        }
        myPeerId = null;
        console.log('Disconnected');
    }
};

function setupConnectionHandlers() {
    connection.on('open', () => {
        console.log('DataChannel opened');
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnDataChannelOpen');
        }
    });

    connection.on('close', () => {
        console.log('DataChannel closed');
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnDataChannelClose');
        }
    });

    connection.on('data', (data) => {
        console.log('Message received:', data);
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnMessageReceived', data);
        }
    });

    connection.on('error', (err) => {
        console.error('Connection error:', err);
    });
}

function generateRoomId() {
    // 6文字の英数字（紛らわしい文字を除外）
    const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    let result = '';
    for (let i = 0; i < 6; i++) {
        result += chars.charAt(Math.floor(Math.random() * chars.length));
    }
    return result;
}
