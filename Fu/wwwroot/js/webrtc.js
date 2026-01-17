// WebRTC P2P Communication using Trystero (Serverless)
// Uses Nostr strategy by default for peer discovery

const NICKNAME_STORAGE_KEY = 'fu_nickname';
const APP_ID = 'fu-shogi-game';

let room = null;
let dotNetRef = null;
let myPeerId = null;  // Trysteroが割り当てる自分のpeer ID（最初の接続時に判明）
let isHost = false;
let myNickname = '';
let currentRoomId = null;
let participants = new Map(); // peerId -> { nickname, isHost }
let trysteroToDotNetId = new Map(); // Trystero peerId -> 我々が使うID（ニックネームベース）
let pendingSelfId = null; // 自分のID（接続前に生成）

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
        // 自分のIDはニックネームベースで生成（一意性のためにランダム文字列を付加）
        pendingSelfId = generateDotNetId(nickname);
        myPeerId = pendingSelfId;

        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnConnectionStateChanged', 'connecting');
        }

        await joinTrysteroRoom(currentRoomId);

        console.log('Room created with ID:', currentRoomId, 'My ID:', myPeerId);
        return currentRoomId;
    },

    // ルームに参加（非ホスト）
    joinRoom: async function (roomId, nickname) {
        myNickname = nickname;
        isHost = false;
        currentRoomId = roomId;
        // 自分のIDはニックネームベースで生成
        pendingSelfId = generateDotNetId(nickname);
        myPeerId = pendingSelfId;

        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnConnectionStateChanged', 'connecting');
        }

        await joinTrysteroRoom(roomId);

        console.log('Joining room:', roomId, 'My ID:', myPeerId);
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
        trysteroToDotNetId.clear();
        myPeerId = null;
        pendingSelfId = null;
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
    onPI((data, tryseteroPeerId) => {
        console.log('Peer info received from', tryseteroPeerId, ':', data);
        const info = JSON.parse(data);
        // 相手が送ってきた自己申告のIDを使用する
        const dotNetId = info.id;

        if (!trysteroToDotNetId.has(tryseteroPeerId)) {
            trysteroToDotNetId.set(tryseteroPeerId, dotNetId);
            participants.set(dotNetId, { nickname: info.nickname, isHost: info.isHost });
            console.log('Mapped Trystero ID', tryseteroPeerId, 'to DotNet ID', dotNetId);
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnParticipantJoinedCallback', dotNetId, info.nickname, info.isHost);
            }
        }
    });

    // 参加者退出受信ハンドラ
    onPL((data, tryseteroPeerId) => {
        console.log('Peer left (Trystero ID):', tryseteroPeerId);
        const dotNetId = trysteroToDotNetId.get(tryseteroPeerId);
        if (dotNetId) {
            participants.delete(dotNetId);
            trysteroToDotNetId.delete(tryseteroPeerId);
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnParticipantLeftCallback', dotNetId);
            }
        }
    });

    // ピア参加ハンドラ
    room.onPeerJoin(tryseteroPeerId => {
        console.log('Peer joined (Trystero ID):', tryseteroPeerId);

        // 自分の情報を送信（自己申告のIDを含める）
        sendPeerInfo(JSON.stringify({
            id: myPeerId,
            nickname: myNickname,
            isHost: isHost
        }));

        // 接続状態を更新（自分を含めて2人になったら接続完了）
        if (dotNetRef && participants.size === 1) {
            dotNetRef.invokeMethodAsync('OnDataChannelOpen');
        }
    });

    // ピア退出ハンドラ
    room.onPeerLeave(tryseteroPeerId => {
        console.log('Peer left (Trystero ID):', tryseteroPeerId);
        const dotNetId = trysteroToDotNetId.get(tryseteroPeerId);

        if (dotNetId) {
            participants.delete(dotNetId);
            trysteroToDotNetId.delete(tryseteroPeerId);

            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnParticipantLeftCallback', dotNetId);

                if (participants.size <= 1) {
                    dotNetRef.invokeMethodAsync('OnDataChannelClose');
                }
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

function generateDotNetId(nickname) {
    // ニックネーム + ランダム文字列で一意性を確保
    const randomPart = Math.random().toString(36).substring(2, 8);
    return `${nickname}_${randomPart}`;
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
