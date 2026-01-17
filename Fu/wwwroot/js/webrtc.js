// WebRTC P2P Communication using PeerJS (Star topology - Host relays messages)

let peer = null;
let connections = new Map(); // peerId -> connection (for host)
let hostConnection = null; // for non-host
let dotNetRef = null;
let myPeerId = null;
let isHost = false;
let myNickname = '';

window.WebRtc = {
    initialize: function (dotNetReference) {
        dotNetRef = dotNetReference;
        console.log('WebRTC (PeerJS) initialized');
    },

    // ルームを作成（ホスト）- 短いルームIDを返す
    createRoom: async function (nickname) {
        return new Promise((resolve, reject) => {
            // 6文字のランダムなルームIDを生成
            const roomId = generateRoomId();
            myNickname = nickname;
            isHost = true;

            peer = new Peer(roomId, {
                debug: 2
            });

            peer.on('open', (id) => {
                console.log('Room created with ID:', id);
                myPeerId = id;

                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnConnectionStateChanged', 'connecting');
                    // ホスト自身を参加者として通知
                    dotNetRef.invokeMethodAsync('OnParticipantJoinedCallback', myPeerId, nickname, true);
                }

                resolve(id);
            });

            peer.on('connection', (conn) => {
                console.log('Peer connected:', conn.peer);
                setupHostConnectionHandlers(conn);
            });

            peer.on('error', (err) => {
                console.error('Peer error:', err);
                if (err.type === 'unavailable-id') {
                    peer.destroy();
                    resolve(window.WebRtc.createRoom(nickname));
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

    // ルームに参加（非ホスト）
    joinRoom: async function (roomId, nickname) {
        return new Promise((resolve, reject) => {
            myNickname = nickname;
            isHost = false;

            peer = new Peer({
                debug: 2
            });

            peer.on('open', (id) => {
                console.log('My peer ID:', id);
                myPeerId = id;

                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnConnectionStateChanged', 'connecting');
                }

                // ホストに接続
                hostConnection = peer.connect(roomId, {
                    reliable: true,
                    metadata: { nickname: nickname }
                });

                setupClientConnectionHandlers(hostConnection);
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

    // メッセージを送信（ホストはブロードキャスト、非ホストはホストに送信）
    sendMessage: function (message) {
        if (isHost) {
            // ホストは全員にブロードキャスト
            let sent = false;
            connections.forEach((conn, peerId) => {
                if (conn.open) {
                    conn.send(message);
                    sent = true;
                }
            });
            console.log('Message broadcast:', message);
            return sent || connections.size === 0; // 自分だけの場合もtrue
        } else {
            // 非ホストはホストに送信
            if (hostConnection && hostConnection.open) {
                hostConnection.send(message);
                console.log('Message sent to host:', message);
                return true;
            }
        }
        console.warn('Connection not ready');
        return false;
    },

    // 特定のピアにメッセージを送信（ホストのみ）
    sendMessageTo: function (peerId, message) {
        if (!isHost) return false;
        const conn = connections.get(peerId);
        if (conn && conn.open) {
            conn.send(message);
            console.log('Message sent to', peerId, ':', message);
            return true;
        }
        return false;
    },

    getConnectionState: function () {
        if (isHost) {
            return connections.size > 0 ? 'connected' : 'connecting';
        }
        if (!hostConnection) return 'disconnected';
        return hostConnection.open ? 'connected' : 'connecting';
    },

    getParticipantCount: function () {
        if (isHost) {
            return connections.size + 1; // +1 for host
        }
        return hostConnection && hostConnection.open ? 2 : 1;
    },

    isHostPeer: function () {
        return isHost;
    },

    getMyPeerId: function () {
        return myPeerId;
    },

    disconnect: function () {
        if (isHost) {
            connections.forEach((conn) => {
                conn.close();
            });
            connections.clear();
        } else if (hostConnection) {
            hostConnection.close();
            hostConnection = null;
        }
        if (peer) {
            peer.destroy();
            peer = null;
        }
        myPeerId = null;
        isHost = false;
        myNickname = '';
        console.log('Disconnected');
    }
};

// ホスト側の接続ハンドラ
function setupHostConnectionHandlers(conn) {
    conn.on('open', () => {
        console.log('DataChannel opened with:', conn.peer);
        connections.set(conn.peer, conn);

        const nickname = conn.metadata?.nickname || 'Guest';

        // 新しい参加者を全員に通知
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnParticipantJoinedCallback', conn.peer, nickname, false);

            // 接続が1つでもあれば接続済み状態に
            if (connections.size === 1) {
                dotNetRef.invokeMethodAsync('OnDataChannelOpen');
            }
        }

        // 新しい参加者に既存の参加者リストを送信
        const participantList = {
            type: 'participantList',
            participants: getParticipantList()
        };
        conn.send(JSON.stringify(participantList));
    });

    conn.on('close', () => {
        console.log('DataChannel closed with:', conn.peer);
        const peerId = conn.peer;
        connections.delete(peerId);

        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnParticipantLeftCallback', peerId);

            if (connections.size === 0) {
                dotNetRef.invokeMethodAsync('OnDataChannelClose');
            }
        }
    });

    conn.on('data', (data) => {
        console.log('Message received from', conn.peer, ':', data);

        // メッセージを他の参加者にリレー
        connections.forEach((otherConn, peerId) => {
            if (peerId !== conn.peer && otherConn.open) {
                otherConn.send(data);
            }
        });

        // 自分にも通知
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnMessageReceived', data);
        }
    });

    conn.on('error', (err) => {
        console.error('Connection error with', conn.peer, ':', err);
    });
}

// 非ホスト側の接続ハンドラ
function setupClientConnectionHandlers(conn) {
    conn.on('open', () => {
        console.log('DataChannel opened with host');
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnDataChannelOpen');
        }
    });

    conn.on('close', () => {
        console.log('DataChannel closed with host');
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnDataChannelClose');
        }
    });

    conn.on('data', (data) => {
        console.log('Message received from host:', data);

        // 参加者リストの処理
        try {
            const parsed = JSON.parse(data);
            if (parsed.type === 'participantList') {
                if (dotNetRef) {
                    parsed.participants.forEach(p => {
                        dotNetRef.invokeMethodAsync('OnParticipantJoinedCallback', p.peerId, p.nickname, p.isHost);
                    });
                }
                return;
            }
            if (parsed.type === 'participantJoined') {
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnParticipantJoinedCallback', parsed.peerId, parsed.nickname, parsed.isHost);
                }
                return;
            }
            if (parsed.type === 'participantLeft') {
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnParticipantLeftCallback', parsed.peerId);
                }
                return;
            }
        } catch (e) {
            // JSON以外のデータ
        }

        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnMessageReceived', data);
        }
    });

    conn.on('error', (err) => {
        console.error('Connection error with host:', err);
    });
}

function getParticipantList() {
    const list = [{ peerId: myPeerId, nickname: myNickname, isHost: true }];
    connections.forEach((conn, peerId) => {
        list.push({
            peerId: peerId,
            nickname: conn.metadata?.nickname || 'Guest',
            isHost: false
        });
    });
    return list;
}

function generateRoomId() {
    const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    let result = '';
    for (let i = 0; i < 6; i++) {
        result += chars.charAt(Math.floor(Math.random() * chars.length));
    }
    return result;
}
