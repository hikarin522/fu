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
    createRoom: async function (nickname, savedPeerId = null) {
        myNickname = nickname;
        isHost = true;
        currentRoomId = generateRoomId();
        // 保存されたPeerIDがあれば再利用、なければ新規生成
        if (savedPeerId) {
            pendingSelfId = savedPeerId;
            console.log('Reusing saved peer ID:', savedPeerId);
        } else {
            pendingSelfId = generateDotNetId(nickname);
        }
        myPeerId = pendingSelfId;

        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnConnectionStateChanged', 'connecting');
        }

        await joinTrysteroRoom(currentRoomId);

        console.log('Room created with ID:', currentRoomId, 'My ID:', myPeerId);
        return currentRoomId;
    },

    // ルームに参加（非ホスト）
    joinRoom: async function (roomId, nickname, savedPeerId = null) {
        myNickname = nickname;
        isHost = false;
        currentRoomId = roomId;
        // 保存されたPeerIDがあれば再利用、なければ新規生成
        if (savedPeerId) {
            pendingSelfId = savedPeerId;
            console.log('Reusing saved peer ID:', savedPeerId);
        } else {
            pendingSelfId = generateDotNetId(nickname);
        }
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

                // 参加者が2人になったら接続完了（peerinfo受信後に通知）
                if (participants.size >= 2) {
                    dotNetRef.invokeMethodAsync('OnDataChannelOpen');
                }
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

        // 注: OnDataChannelOpenはpeerinfo受信後に呼ばれる（onPIハンドラ内）
    });

    // ピア退出ハンドラ
    room.onPeerLeave(tryseteroPeerId => {
        console.log('Peer left (Trystero ID):', tryseteroPeerId);
        const dotNetId = trysteroToDotNetId.get(tryseteroPeerId);

        if (dotNetId) {
            // 退出したピアがホストかどうかを確認
            const leftParticipant = participants.get(dotNetId);
            const wasHost = leftParticipant?.isHost ?? false;

            participants.delete(dotNetId);
            trysteroToDotNetId.delete(tryseteroPeerId);

            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnParticipantLeftCallback', dotNetId);

                // ホストが退出した場合、自分がホストを引き継ぐ
                if (wasHost && !isHost) {
                    isHost = true;
                    // 自分の参加者情報を更新
                    const myInfo = participants.get(myPeerId);
                    if (myInfo) {
                        myInfo.isHost = true;
                    }
                    console.log('Host left, becoming new host:', myPeerId);
                    dotNetRef.invokeMethodAsync('OnBecameHostCallback');
                }
                // 注: ルームは維持し続ける（相手が再接続してくる可能性があるため）
                // OnDataChannelCloseは呼ばない
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

// Turn notification sound
let audioContext = null;

window.TurnNotification = {
    play: function () {
        try {
            // AudioContextは初回ユーザー操作後に作成する必要がある
            if (!audioContext) {
                audioContext = new (window.AudioContext || window.webkitAudioContext)();
            }

            // AudioContextがsuspended状態なら再開
            if (audioContext.state === 'suspended') {
                audioContext.resume();
            }

            const now = audioContext.currentTime;

            // 2つの音を重ねて和音のような通知音を作成
            // 高い音（C5 = 523.25Hz）
            const osc1 = audioContext.createOscillator();
            const gain1 = audioContext.createGain();
            osc1.type = 'sine';
            osc1.frequency.value = 523.25;
            gain1.gain.setValueAtTime(0.3, now);
            gain1.gain.exponentialRampToValueAtTime(0.01, now + 0.3);
            osc1.connect(gain1);
            gain1.connect(audioContext.destination);
            osc1.start(now);
            osc1.stop(now + 0.3);

            // 低い音（G4 = 392Hz）少し遅れて
            const osc2 = audioContext.createOscillator();
            const gain2 = audioContext.createGain();
            osc2.type = 'sine';
            osc2.frequency.value = 392;
            gain2.gain.setValueAtTime(0.2, now + 0.05);
            gain2.gain.exponentialRampToValueAtTime(0.01, now + 0.35);
            osc2.connect(gain2);
            gain2.connect(audioContext.destination);
            osc2.start(now + 0.05);
            osc2.stop(now + 0.35);

        } catch (e) {
            console.warn('Failed to play turn notification sound:', e);
        }
    }
};

// Sound settings storage
const SOUND_SETTINGS_KEY = 'fu_sound_enabled';

// Game session storage (for reconnection)
const GAME_SESSION_KEY = 'fu_game_session';

window.GameSession = {
    save: function (roomId, nickname, peerId) {
        try {
            const session = {
                roomId: roomId,
                nickname: nickname,
                peerId: peerId,
                timestamp: Date.now()
            };
            localStorage.setItem(GAME_SESSION_KEY, JSON.stringify(session));
        } catch (e) {
            console.warn('Failed to save game session to localStorage:', e);
        }
    },

    load: function () {
        try {
            const data = localStorage.getItem(GAME_SESSION_KEY);
            if (!data) return null;

            const session = JSON.parse(data);
            // セッションが24時間以上古い場合は無効とみなす
            const maxAge = 24 * 60 * 60 * 1000;
            if (Date.now() - session.timestamp > maxAge) {
                localStorage.removeItem(GAME_SESSION_KEY);
                return null;
            }
            return session;
        } catch (e) {
            console.warn('Failed to load game session from localStorage:', e);
            return null;
        }
    },

    clear: function () {
        try {
            localStorage.removeItem(GAME_SESSION_KEY);
        } catch (e) {
            console.warn('Failed to clear game session from localStorage:', e);
        }
    }
};

window.SoundSettings = {
    save: function (value) {
        try {
            localStorage.setItem(SOUND_SETTINGS_KEY, value);
        } catch (e) {
            console.warn('Failed to save sound setting to localStorage:', e);
        }
    },
    load: function () {
        try {
            return localStorage.getItem(SOUND_SETTINGS_KEY);
        } catch (e) {
            console.warn('Failed to load sound setting from localStorage:', e);
            return null;
        }
    }
};
