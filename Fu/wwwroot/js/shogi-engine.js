// YaneuraOu WASM Engine wrapper for Blazor
let engine = null;
let engineReady = false;
let dotNetReference = null;

// Initialize the engine
async function initShogiEngine() {
    if (engine) {
        return engineReady;
    }

    try {
        // Load YaneuraOu from local lib
        const script = document.createElement('script');
        script.src = 'lib/yaneuraou/yaneuraou.js';
        document.head.appendChild(script);

        await new Promise((resolve, reject) => {
            script.onload = resolve;
            script.onerror = reject;
        });

        // YaneuraOu is a factory function that returns a module instance
        // Configure locateFile to find .wasm and .data files in the correct directory
        const yaneuraou = await YaneuraOu({
            locateFile: (path) => `lib/yaneuraou/${path}`
        });

        // Set up message listener
        yaneuraou.addMessageListener((line) => {
            console.log('Engine:', line);
            if (dotNetReference) {
                dotNetReference.invokeMethodAsync('OnEngineMessage', line);
            }
        });

        // エンジンを先に設定
        engine = yaneuraou;
        engineReady = true;
        console.log('YaneuraOu engine initialized');

        // USI初期化とusiok待機
        await new Promise((resolve) => {
            let resolved = false;
            yaneuraou.addMessageListener((line) => {
                if (!resolved && line === 'usiok') {
                    resolved = true;
                    resolve();
                }
            });
            yaneuraou.postMessage('usi');
        });

        console.log('USI handshake complete');
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

// Request evaluation for a position
// depth: null or 0 = infinite search, positive number = depth limit
function requestEvaluation(sfen, depth) {
    if (!engine || !engineReady) {
        console.warn('Engine not ready');
        return false;
    }

    // Stop any ongoing search
    engine.postMessage('stop');

    // Set position
    engine.postMessage('position sfen ' + sfen);

    // Start search
    if (depth && depth > 0) {
        engine.postMessage('go depth ' + depth);
    } else {
        // Infinite search until stopped
        engine.postMessage('go infinite');
    }

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
    isCrossOriginIsolated: isCrossOriginIsolated
};
