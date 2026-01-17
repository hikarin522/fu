// WebRTC P2P Communication using Trystero (Serverless)
// Uses Nostr strategy by default for peer discovery

const NICKNAME_STORAGE_KEY = 'fu_nickname';
const APP_ID = 'fu-shogi-game';

let room = null;
let dotNetRef = null;
let myPeerId = null;
let isHost = false;
let myNickname = '';
let currentRoomId = null;
let participants = new Map(); // peerId -> { nickname, isHost }

// Trystero actions (names must be <= 12 bytes)
let sendMessage = null;
let sendPeerInfo = null;
let sendPeerLeft = null;

window.WebRtc = {
    initialize: function (dotNetReference) {
        dotNetRef = dotNetReference;
        console.log('WebRTC (Trystero) initialized');
    },

    // ルームを作成（ホスト）
    createRoom: async function (nickname) {
        myNickname = nickname;
        isHost = true;
        currentRoomId = generateRoomId();
        myPeerId = generatePeerId();

        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnConnectionStateChanged', 'connecting');
        }

        await joinTrysteroRoom(currentRoomId);

        console.log('Room created with ID:', currentRoomId);
        return currentRoomId;
    },

    // ルームに参加（非ホスト）
    joinRoom: async function (roomId, nickname) {
        myNickname = nickname;
        isHost = false;
        currentRoomId = roomId;
        myPeerId = generatePeerId();

        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnConnectionStateChanged', 'connecting');
        }

        await joinTrysteroRoom(roomId);

        console.log('Joining room:', roomId);
        return true;
    },

    // メッセージを送信（全員にブロードキャスト）
    sendMessage: function (message) {
        if (sendMessage && room) {
            sendMessage(message);
            console.log('Message broadcast:', message);
            return true;
        }
        console.warn('Room not ready');
        return false;
    },

    // 特定のピアにメッセージを送信
    sendMessageTo: function (peerId, message) {
        if (sendMessage && room) {
            sendMessage(message, [peerId]);
            console.log('Message sent to', peerId, ':', message);
            return true;
        }
        return false;
    },

    getConnectionState: function () {
        if (!room) return 'disconnected';
        return participants.size > 1 ? 'connected' : 'connecting';
    },

    getParticipantCount: function () {
        return participants.size;
    },

    isHostPeer: function () {
        return isHost;
    },

    getMyPeerId: function () {
        return myPeerId;
    },

    disconnect: function () {
        if (room) {
            room.leave();
            room = null;
        }
        sendMessage = null;
        sendPeerInfo = null;
        sendPeerLeft = null;
        participants.clear();
        myPeerId = null;
        isHost = false;
        myNickname = '';
        currentRoomId = null;
        console.log('Disconnected');
    }
};

async function joinTrysteroRoom(roomId) {
    // Trystero uses dynamic import
    const { joinRoom } = await import('https://esm.sh/trystero/nostr');

    const config = { appId: APP_ID };
    room = joinRoom(config, roomId);

    // メッセージアクションを設定 (names must be <= 12 bytes)
    const [sendMsg, onMsg] = room.makeAction('msg');
    sendMessage = sendMsg;

    const [sendPI, onPI] = room.makeAction('peerinfo');
    sendPeerInfo = sendPI;

    const [sendPL, onPL] = room.makeAction('peerleft');
    sendPeerLeft = sendPL;

    // メッセージ受信ハンドラ
    onMsg((data, peerId) => {
        console.log('Message received from', peerId, ':', data);
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnMessageReceived', data);
        }
    });

    // 参加者情報受信ハンドラ
    onPI((data, peerId) => {
        console.log('Peer info received from', peerId, ':', data);
        const info = JSON.parse(data);
        if (!participants.has(peerId)) {
            participants.set(peerId, { nickname: info.nickname, isHost: info.isHost });
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnParticipantJoinedCallback', peerId, info.nickname, info.isHost);
            }
        }
    });

    // 参加者退出受信ハンドラ
    onPL((data, peerId) => {
        console.log('Peer left:', peerId);
        participants.delete(peerId);
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnParticipantLeftCallback', peerId);
        }
    });

    // ピア参加ハンドラ
    room.onPeerJoin(peerId => {
        console.log('Peer joined:', peerId);

        // 自分の情報を送信
        sendPeerInfo(JSON.stringify({
            nickname: myNickname,
            isHost: isHost
        }));

        // 接続状態を更新
        if (dotNetRef && participants.size === 1) {
            dotNetRef.invokeMethodAsync('OnDataChannelOpen');
        }
    });

    // ピア退出ハンドラ
    room.onPeerLeave(peerId => {
        console.log('Peer left:', peerId);
        const participant = participants.get(peerId);
        participants.delete(peerId);

        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnParticipantLeftCallback', peerId);

            if (participants.size <= 1) {
                dotNetRef.invokeMethodAsync('OnDataChannelClose');
            }
        }
    });

    // 自分を参加者として追加
    participants.set(myPeerId, { nickname: myNickname, isHost: isHost });

    // 自分の参加を通知
    if (dotNetRef) {
        dotNetRef.invokeMethodAsync('OnParticipantJoinedCallback', myPeerId, myNickname, isHost);
    }
}

function generateRoomId() {
    const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    let result = '';
    for (let i = 0; i < 6; i++) {
        result += chars.charAt(Math.floor(Math.random() * chars.length));
    }
    return result;
}

function generatePeerId() {
    return 'peer_' + Math.random().toString(36).substring(2, 11);
}

// Nickname storage
window.NicknameStorage = {
    save: function (nickname) {
        try {
            localStorage.setItem(NICKNAME_STORAGE_KEY, nickname);
        } catch (e) {
            console.warn('Failed to save nickname to localStorage:', e);
        }
    },

    load: function () {
        try {
            return localStorage.getItem(NICKNAME_STORAGE_KEY) || '';
        } catch (e) {
            console.warn('Failed to load nickname from localStorage:', e);
            return '';
        }
    }
};
