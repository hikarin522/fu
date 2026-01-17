// YaneuraOu WASM Engine wrapper for Blazor
let engine = null;
let engineReady = false;
let messageListeners = [];
let dotNetReference = null;

// Initialize the engine
async function initShogiEngine() {
    if (engine) {
        return engineReady;
    }

    try {
        // Load YaneuraOu from CDN
        const script = document.createElement('script');
        script.src = 'https://cdn.jsdelivr.net/npm/yaneuraou.wasm@0.1.2/yaneuraou.js';
        document.head.appendChild(script);

        await new Promise((resolve, reject) => {
            script.onload = resolve;
            script.onerror = reject;
        });

        // Wait for module to be ready
        await YaneuraOu.ready;

        // Set up message listener
        YaneuraOu.addMessageListener((line) => {
            console.log('Engine:', line);
            if (dotNetReference) {
                dotNetReference.invokeMethodAsync('OnEngineMessage', line);
            }
        });

        // Initialize USI
        YaneuraOu.postMessage('usi');

        engineReady = true;
        engine = YaneuraOu;
        console.log('YaneuraOu engine initialized');
        return true;
    } catch (error) {
        console.error('Failed to initialize shogi engine:', error);
        return false;
    }
}

// Set the .NET reference for callbacks
function setEngineCallback(dotNetRef) {
    dotNetReference = dotNetRef;
}

// Send a command to the engine
function sendEngineCommand(command) {
    if (engine && engineReady) {
        console.log('Sending to engine:', command);
        engine.postMessage(command);
        return true;
    }
    return false;
}

// Convert board position to SFEN format
function boardToSfen(boardData, currentPlayer, senteCaptured, goteCaptured, moveCount) {
    // boardData is a 2D array [row][col] of piece strings or null
    // Piece format: "P" for pawn, "+P" for promoted pawn, etc.
    // Uppercase = Sente, Lowercase = Gote

    let sfen = '';

    // Board position
    for (let row = 0; row < 9; row++) {
        let emptyCount = 0;
        for (let col = 0; col < 9; col++) {
            const piece = boardData[row * 9 + col];
            if (piece) {
                if (emptyCount > 0) {
                    sfen += emptyCount;
                    emptyCount = 0;
                }
                sfen += piece;
            } else {
                emptyCount++;
            }
        }
        if (emptyCount > 0) {
            sfen += emptyCount;
        }
        if (row < 8) {
            sfen += '/';
        }
    }

    // Current player
    sfen += ' ' + (currentPlayer === 0 ? 'b' : 'w');

    // Captured pieces
    let captured = '';
    if (senteCaptured) captured += senteCaptured;
    if (goteCaptured) captured += goteCaptured;
    sfen += ' ' + (captured || '-');

    // Move count
    sfen += ' ' + (moveCount || 1);

    return sfen;
}

// Request evaluation for a position
function requestEvaluation(sfen, depth) {
    if (!engine || !engineReady) {
        console.warn('Engine not ready');
        return false;
    }

    // Stop any ongoing search
    engine.postMessage('stop');

    // Set position
    engine.postMessage('position sfen ' + sfen);

    // Start search with specified depth
    engine.postMessage('go depth ' + (depth || 10));

    return true;
}

// Stop the current search
function stopEngine() {
    if (engine && engineReady) {
        engine.postMessage('stop');
        return true;
    }
    return false;
}

// Check if SharedArrayBuffer is available (needed for multi-threading)
function isSharedArrayBufferAvailable() {
    return typeof SharedArrayBuffer !== 'undefined';
}

// Check if the page is cross-origin isolated
function isCrossOriginIsolated() {
    return window.crossOriginIsolated === true;
}

// Export functions for Blazor
window.ShogiEngine = {
    init: initShogiEngine,
    setCallback: setEngineCallback,
    sendCommand: sendEngineCommand,
    requestEvaluation: requestEvaluation,
    stop: stopEngine,
    isSharedArrayBufferAvailable: isSharedArrayBufferAvailable,
    isCrossOriginIsolated: isCrossOriginIsolated,
    boardToSfen: boardToSfen
};
