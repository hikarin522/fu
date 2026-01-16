// WebRTC P2P Communication for Shogi Game

let peerConnection = null;
let dataChannel = null;
let dotNetRef = null;

const config = {
    iceServers: [
        { urls: 'stun:stun.l.google.com:19302' },
        { urls: 'stun:stun1.l.google.com:19302' },
        { urls: 'stun:stun2.l.google.com:19302' }
    ]
};

window.WebRtc = {
    initialize: function (dotNetReference) {
        dotNetRef = dotNetReference;
        console.log('WebRTC initialized');
    },

    createOffer: async function () {
        try {
            peerConnection = new RTCPeerConnection(config);
            setupPeerConnectionHandlers();

            // Create data channel (only offerer creates it)
            dataChannel = peerConnection.createDataChannel('shogi', {
                ordered: true
            });
            setupDataChannelHandlers();

            const offer = await peerConnection.createOffer();
            await peerConnection.setLocalDescription(offer);

            // Wait for ICE gathering to complete
            await waitForIceGathering();

            const fullOffer = JSON.stringify(peerConnection.localDescription);
            console.log('Offer created');
            return fullOffer;
        } catch (error) {
            console.error('Error creating offer:', error);
            throw error;
        }
    },

    createAnswer: async function (offerJson) {
        try {
            peerConnection = new RTCPeerConnection(config);
            setupPeerConnectionHandlers();

            // Answer side receives data channel
            peerConnection.ondatachannel = (event) => {
                dataChannel = event.channel;
                setupDataChannelHandlers();
            };

            const offer = JSON.parse(offerJson);
            await peerConnection.setRemoteDescription(new RTCSessionDescription(offer));

            const answer = await peerConnection.createAnswer();
            await peerConnection.setLocalDescription(answer);

            // Wait for ICE gathering to complete
            await waitForIceGathering();

            const fullAnswer = JSON.stringify(peerConnection.localDescription);
            console.log('Answer created');
            return fullAnswer;
        } catch (error) {
            console.error('Error creating answer:', error);
            throw error;
        }
    },

    acceptAnswer: async function (answerJson) {
        try {
            const answer = JSON.parse(answerJson);
            await peerConnection.setRemoteDescription(new RTCSessionDescription(answer));
            console.log('Answer accepted');
            return true;
        } catch (error) {
            console.error('Error accepting answer:', error);
            throw error;
        }
    },

    sendMessage: function (message) {
        if (dataChannel && dataChannel.readyState === 'open') {
            dataChannel.send(message);
            console.log('Message sent:', message);
            return true;
        }
        console.warn('DataChannel not ready');
        return false;
    },

    getConnectionState: function () {
        if (!peerConnection) return 'disconnected';
        return peerConnection.connectionState || 'unknown';
    },

    disconnect: function () {
        if (dataChannel) {
            dataChannel.close();
            dataChannel = null;
        }
        if (peerConnection) {
            peerConnection.close();
            peerConnection = null;
        }
        console.log('Disconnected');
    }
};

function setupPeerConnectionHandlers() {
    peerConnection.onicecandidate = (event) => {
        if (event.candidate) {
            console.log('ICE candidate:', event.candidate.candidate);
        }
    };

    peerConnection.onconnectionstatechange = () => {
        console.log('Connection state:', peerConnection.connectionState);
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnConnectionStateChanged', peerConnection.connectionState);
        }
    };

    peerConnection.oniceconnectionstatechange = () => {
        console.log('ICE connection state:', peerConnection.iceConnectionState);
    };
}

function setupDataChannelHandlers() {
    dataChannel.onopen = () => {
        console.log('DataChannel opened');
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnDataChannelOpen');
        }
    };

    dataChannel.onclose = () => {
        console.log('DataChannel closed');
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnDataChannelClose');
        }
    };

    dataChannel.onmessage = (event) => {
        console.log('Message received:', event.data);
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnMessageReceived', event.data);
        }
    };

    dataChannel.onerror = (error) => {
        console.error('DataChannel error:', error);
    };
}

function waitForIceGathering() {
    return new Promise((resolve) => {
        if (peerConnection.iceGatheringState === 'complete') {
            resolve();
            return;
        }

        const checkState = () => {
            if (peerConnection.iceGatheringState === 'complete') {
                peerConnection.removeEventListener('icegatheringstatechange', checkState);
                resolve();
            }
        };

        peerConnection.addEventListener('icegatheringstatechange', checkState);

        // Timeout after 5 seconds
        setTimeout(() => {
            peerConnection.removeEventListener('icegatheringstatechange', checkState);
            resolve();
        }, 5000);
    });
}
