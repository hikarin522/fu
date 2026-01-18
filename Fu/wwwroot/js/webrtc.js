// WebRTC P2P Communication using Trystero (Serverless)
// Trystero ESMの動的インポートとイベントハンドラ登録のみJS側で行う
// ID生成・ホスト選出・Storage管理はC#側で行う

const APP_ID = 'fu-shogi-game';

let room = null;
let dotNetRef = null;
let myPeerId = null;
let isHost = false;
let myNickname = '';

// Trystero内部ID → C#側ID のマッピング
// (Trysteroは独自のpeerIdを生成するため、C#で生成したIDと対応付けが必要)
let trysteroToDotNetId = new Map();
let participants = new Map();

// Trystero actions
let sendMessage = null;
let sendPeerInfo = null;

window.WebRtc = {
    // 初期化（コールバック登録）
    initialize: function (dotNetReference) {
        dotNetRef = dotNetReference;
    },

    // ルームに参加（createRoom/joinRoomを統合）
    joinRoom: async function (roomId, peerId, nickname, asHost) {
        myPeerId = peerId;
        myNickname = nickname;
        isHost = asHost;

        dotNetRef?.invokeMethodAsync('OnConnectionStateChangedCallback', 'connecting');

        // Trystero ESMを動的インポート
        const { joinRoom } = await import('https://esm.sh/trystero/nostr');
        room = joinRoom({ appId: APP_ID }, roomId);

        // メッセージアクション (名前は12バイト以下)
        const [sendMsg, onMsg] = room.makeAction('msg');
        const [sendPI, onPI] = room.makeAction('peerinfo');
        sendMessage = sendMsg;
        sendPeerInfo = sendPI;

        // メッセージ受信 → C#に転送
        onMsg((data, _) => {
            dotNetRef?.invokeMethodAsync('OnMessageReceived', data);
        });

        // 参加者情報受信
        onPI((data, tryseteroPeerId) => {
            const info = JSON.parse(data);
            const dotNetId = info.id;
            const isKnown = trysteroToDotNetId.has(tryseteroPeerId);

            if (!isKnown) {
                // 新規参加者: ホスト重複チェック
                let effectiveIsHost = info.isHost;
                if (effectiveIsHost && [...participants.values()].some(p => p.isHost)) {
                    effectiveIsHost = false;
                }

                trysteroToDotNetId.set(tryseteroPeerId, dotNetId);
                participants.set(dotNetId, { nickname: info.nickname, isHost: effectiveIsHost });

                dotNetRef?.invokeMethodAsync('OnParticipantJoinedCallback', dotNetId, info.nickname, effectiveIsHost);
                if (participants.size >= 2) {
                    dotNetRef?.invokeMethodAsync('OnDataChannelOpen');
                }
            } else {
                // ホスト変更通知
                const existing = participants.get(dotNetId);
                if (existing && info.isHost && !existing.isHost) {
                    if (isHost) {
                        isHost = false;
                        const myInfo = participants.get(myPeerId);
                        if (myInfo) myInfo.isHost = false;
                        dotNetRef?.invokeMethodAsync('OnHostStatusChanged', false);
                    }
                    existing.isHost = true;
                }
            }
        });

        // ピア参加 → 自分の情報を送信
        room.onPeerJoin(_ => {
            sendPeerInfo(JSON.stringify({ id: myPeerId, nickname: myNickname, isHost }));
        });

        // ピア退出 → C#に通知（ホスト選出はC#側）
        room.onPeerLeave(tryseteroPeerId => {
            const dotNetId = trysteroToDotNetId.get(tryseteroPeerId);
            if (dotNetId) {
                const wasHost = participants.get(dotNetId)?.isHost ?? false;
                participants.delete(dotNetId);
                trysteroToDotNetId.delete(tryseteroPeerId);
                dotNetRef?.invokeMethodAsync('OnParticipantLeftCallback', dotNetId, wasHost);
            }
        });

        // 自分を追加
        participants.set(myPeerId, { nickname: myNickname, isHost });
        dotNetRef?.invokeMethodAsync('OnParticipantJoinedCallback', myPeerId, myNickname, isHost);
    },

    // メッセージ送信
    sendMessage: function (message) {
        if (sendMessage && room) {
            sendMessage(message);
            return true;
        }
        return false;
    },

    // ホスト昇格を他ピアに通知
    notifyBecameHost: function () {
        isHost = true;
        const myInfo = participants.get(myPeerId);
        if (myInfo) myInfo.isHost = true;
        sendPeerInfo?.(JSON.stringify({ id: myPeerId, nickname: myNickname, isHost: true }));
    },

    // 切断
    disconnect: function () {
        room?.leave();
        room = null;
        sendMessage = null;
        sendPeerInfo = null;
        participants.clear();
        trysteroToDotNetId.clear();
        myPeerId = null;
        isHost = false;
        myNickname = '';
    },

    // 接続状態（C#側でも管理しているが、互換性のため残す）
    getConnectionState: () => !room ? 'disconnected' : participants.size > 1 ? 'connected' : 'connecting',
    getParticipantCount: () => participants.size,
    isHostPeer: () => isHost
};

// 通知音 (AudioContext APIはJS側でのみ使用可能)
let audioContext = null;

window.TurnNotification = {
    play: function () {
        try {
            if (!audioContext) {
                audioContext = new (window.AudioContext || window.webkitAudioContext)();
            }
            if (audioContext.state === 'suspended') {
                audioContext.resume();
            }

            const now = audioContext.currentTime;

            // 高音 (C5)
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

            // 低音 (G4)
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
            console.warn('Failed to play notification sound:', e);
        }
    }
};

