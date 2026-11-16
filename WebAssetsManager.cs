using System;
using System.IO;
using System.Text;

namespace MyOverlay;

public static class WebAssetsManager
{
    public const string VirtualHostName = "overlay.local";
    public const string MicTestUrl = "https://overlay.local/mic-test.html";

    private static readonly string AssetsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyOverlay",
        "WebAssets"
    );

    public static string GetAssetsDirectory()
    {
        EnsureAssets();
        return AssetsDirectory;
    }

    public static void EnsureAssets()
    {
        try
        {
            if (!Directory.Exists(AssetsDirectory))
            {
                Directory.CreateDirectory(AssetsDirectory);
            }

            string htmlPath = Path.Combine(AssetsDirectory, "mic-test.html");
            File.WriteAllText(htmlPath, GetMicTestHtml(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebAssetsManager] Error ensuring assets: {ex.Message}");
        }
    }

    public static string GetMicTestHtml()
    {
        return @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>MyOverlay Audio Routing & Microphone VU Meter</title>
    <style>
        :root {
            --bg-main: #121217;
            --bg-card: #1c1c24;
            --bg-card-header: #262632;
            --accent-primary: #6366f1;
            --accent-hover: #4f46e5;
            --accent-green: #10b981;
            --accent-yellow: #f59e0b;
            --accent-red: #ef4444;
            --text-main: #f8fafc;
            --text-muted: #94a3b8;
            --border-color: #2e2e3d;
        }

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
            font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif;
        }

        body {
            background-color: var(--bg-main);
            color: var(--text-main);
            padding: 24px;
            display: flex;
            flex-direction: column;
            align-items: center;
            min-height: 100vh;
        }

        .container {
            max-width: 860px;
            width: 100%;
            display: flex;
            flex-direction: column;
            gap: 20px;
        }

        header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            padding-bottom: 16px;
            border-bottom: 1px solid var(--border-color);
        }

        h1 {
            font-size: 1.45rem;
            font-weight: 700;
            display: flex;
            align-items: center;
            gap: 10px;
        }

        .badge {
            font-size: 0.75rem;
            background-color: var(--accent-primary);
            color: white;
            padding: 3px 8px;
            border-radius: 9999px;
            font-weight: 600;
            letter-spacing: 0.5px;
            text-transform: uppercase;
        }

        .card {
            background-color: var(--bg-card);
            border: 1px solid var(--border-color);
            border-radius: 10px;
            overflow: hidden;
            box-shadow: 0 4px 12px rgba(0, 0, 0, 0.25);
        }

        .card-header {
            background-color: var(--bg-card-header);
            padding: 12px 18px;
            font-size: 0.92rem;
            font-weight: 600;
            color: var(--text-muted);
            border-bottom: 1px solid var(--border-color);
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        .card-body {
            padding: 20px;
            display: flex;
            flex-direction: column;
            gap: 16px;
        }

        .controls-row {
            display: flex;
            flex-wrap: wrap;
            gap: 12px;
            align-items: center;
        }

        button {
            background-color: var(--accent-primary);
            color: white;
            border: none;
            padding: 10px 18px;
            border-radius: 6px;
            font-size: 0.95rem;
            font-weight: 600;
            cursor: pointer;
            transition: background 0.15s ease;
            display: inline-flex;
            align-items: center;
            gap: 8px;
        }

        button:hover:not(:disabled) {
            background-color: var(--accent-hover);
        }

        button:disabled {
            opacity: 0.5;
            cursor: not-allowed;
        }

        button.btn-stop {
            background-color: var(--accent-red);
        }

        button.btn-stop:hover:not(:disabled) {
            background-color: #dc2626;
        }

        select {
            background-color: #242430;
            color: var(--text-main);
            border: 1px solid var(--border-color);
            padding: 9px 12px;
            border-radius: 6px;
            font-size: 0.9rem;
            outline: none;
            min-width: 240px;
        }

        .status-pill {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            padding: 4px 10px;
            border-radius: 20px;
            font-size: 0.82rem;
            font-weight: 600;
        }

        .status-inactive {
            background-color: rgba(239, 68, 68, 0.15);
            color: var(--accent-red);
            border: 1px solid rgba(239, 68, 68, 0.3);
        }

        .status-active {
            background-color: rgba(16, 185, 129, 0.15);
            color: var(--accent-green);
            border: 1px solid rgba(16, 185, 129, 0.3);
        }

        .dot {
            width: 8px;
            height: 8px;
            border-radius: 50%;
            background-color: currentColor;
        }

        .active-dot {
            animation: pulse 1.5s infinite;
        }

        @keyframes pulse {
            0%, 100% { opacity: 1; transform: scale(1); }
            50% { opacity: 0.4; transform: scale(1.2); }
        }

        /* Meter styles */
        .meter-container {
            display: flex;
            flex-direction: column;
            gap: 8px;
        }

        .meter-labels {
            display: flex;
            justify-content: space-between;
            font-size: 0.8rem;
            color: var(--text-muted);
        }

        .vu-track {
            width: 100%;
            height: 22px;
            background-color: #1a1a22;
            border: 1px solid var(--border-color);
            border-radius: 4px;
            position: relative;
            overflow: hidden;
        }

        .vu-fill {
            height: 100%;
            width: 0%;
            background: linear-gradient(to right, #10b981 0%, #10b981 65%, #f59e0b 85%, #ef4444 100%);
            transition: width 0.05s linear;
        }

        .vu-peak-line {
            position: absolute;
            top: 0;
            bottom: 0;
            width: 3px;
            background-color: #ffffff;
            opacity: 0.9;
            left: 0%;
            transition: left 0.15s ease-out;
        }

        /* Oscilloscope Canvas */
        .canvas-container {
            width: 100%;
            height: 140px;
            background-color: #0b0b0f;
            border: 1px solid var(--border-color);
            border-radius: 6px;
            overflow: hidden;
        }

        canvas {
            width: 100%;
            height: 100%;
            display: block;
        }

        /* Metadata Grid */
        .metadata-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
            gap: 12px;
        }

        .meta-item {
            background-color: #16161d;
            border: 1px solid var(--border-color);
            border-radius: 6px;
            padding: 10px 14px;
            display: flex;
            flex-direction: column;
            gap: 4px;
        }

        .meta-title {
            font-size: 0.75rem;
            text-transform: uppercase;
            color: var(--text-muted);
            font-weight: 600;
            letter-spacing: 0.5px;
        }

        .meta-value {
            font-size: 0.95rem;
            color: var(--text-main);
            font-weight: 500;
            word-break: break-all;
        }

        .meta-value.highlight {
            color: #818cf8;
            font-weight: 700;
        }

        .log-box {
            background-color: #0d0d12;
            border: 1px solid var(--border-color);
            border-radius: 6px;
            padding: 12px;
            font-family: ""Consolas"", ""Courier New"", monospace;
            font-size: 0.8rem;
            color: #cbd5e1;
            height: 110px;
            overflow-y: auto;
            display: flex;
            flex-direction: column-reverse;
        }

        .log-entry {
            margin-bottom: 3px;
            line-height: 1.4;
        }
        .log-entry.info { color: #94a3b8; }
        .log-entry.success { color: #34d399; }
        .log-entry.warn { color: #fbbf24; }
        .log-entry.error { color: #f87171; }
    </style>
</head>
<body>
    <div class=""container"">
        <header>
            <h1>
                <span>🎙️</span>
                <span>MyOverlay Audio Routing Diagnostics</span>
            </h1>
            <span class=""badge"">Chromium getUserMedia</span>
        </header>

        <div class=""card"">
            <div class=""card-header"">
                <span>Microphone Capture Controls</span>
                <span id=""statusPill"" class=""status-pill status-inactive"">
                    <span class=""dot""></span>
                    <span id=""statusText"">Inactive</span>
                </span>
            </div>
            <div class=""card-body"">
                <div class=""controls-row"">
                    <button id=""btnStart"">
                        <span>▶</span>
                        <span>Start Test (getUserMedia)</span>
                    </button>
                    <button id=""btnStop"" class=""btn-stop"" disabled>
                        <span>⏹</span>
                        <span>Stop Test</span>
                    </button>
                    <select id=""cboDevices"">
                        <option value="""">Auto (Default Windows Recording Device)</option>
                    </select>
                </div>

                <div class=""controls-row"">
                    <label style=""font-size: 0.85rem; color: var(--text-muted); display: flex; align-items: center; gap: 8px;"">
                        <input type=""checkbox"" id=""chkCleanAudio"">
                        <span>Disable WebRTC Echo Cancellation & Noise Suppression (Raw Digital Stream)</span>
                    </label>
                </div>
            </div>
        </div>

        <div class=""card"">
            <div class=""card-header"">
                <span>Live Audio VU Meter & Oscilloscope Waveform</span>
                <span id=""timerText"" style=""font-size: 0.85rem; color: #a5b4fc;"">Streaming: 00:00</span>
            </div>
            <div class=""card-body"">
                <div class=""meter-container"">
                    <div class=""meter-labels"">
                        <span>-60 dB</span>
                        <span>-30 dB</span>
                        <span>-18 dB</span>
                        <span>-6 dB</span>
                        <span id=""dbDisplay"">0.0 dB</span>
                    </div>
                    <div class=""vu-track"">
                        <div id=""vuFill"" class=""vu-fill""></div>
                        <div id=""vuPeak"" class=""vu-peak-line""></div>
                    </div>
                </div>

                <div class=""canvas-container"">
                    <canvas id=""oscilloscope""></canvas>
                </div>
            </div>
        </div>

        <div class=""card"">
            <div class=""card-header"">
                <span>Active Track & WebRTC Telemetry</span>
                <span id=""fpsIndicator"" style=""font-size: 0.8rem; color: var(--text-muted);"">Visualizer: 60 FPS</span>
            </div>
            <div class=""card-body"">
                <div class=""metadata-grid"">
                    <div class=""meta-item"">
                        <span class=""meta-title"">Active Device Label</span>
                        <span id=""metaDevice"" class=""meta-value highlight"">None</span>
                    </div>
                    <div class=""meta-item"">
                        <span class=""meta-title"">Stream Active / Enabled</span>
                        <span id=""metaActive"" class=""meta-value"">False</span>
                    </div>
                    <div class=""meta-item"">
                        <span class=""meta-title"">Ready State</span>
                        <span id=""metaReady"" class=""meta-value"">None</span>
                    </div>
                    <div class=""meta-item"">
                        <span class=""meta-title"">Sample Rate / Channels</span>
                        <span id=""metaFormat"" class=""meta-value"">None</span>
                    </div>
                </div>

                <div class=""log-box"" id=""logBox"">
                    <div class=""log-entry info"">[Ready] Click 'Start Test' to request browser microphone input.</div>
                </div>
            </div>
        </div>
    </div>

    <script>
        let audioContext = null;
        let mediaStream = null;
        let sourceNode = null;
        let analyserNode = null;
        let animFrameId = null;
        let startTime = null;
        let timerInterval = null;
        let peakLevel = 0;

        const btnStart = document.getElementById('btnStart');
        const btnStop = document.getElementById('btnStop');
        const cboDevices = document.getElementById('cboDevices');
        const chkCleanAudio = document.getElementById('chkCleanAudio');
        const statusPill = document.getElementById('statusPill');
        const statusText = document.getElementById('statusText');
        const timerText = document.getElementById('timerText');
        const vuFill = document.getElementById('vuFill');
        const vuPeak = document.getElementById('vuPeak');
        const dbDisplay = document.getElementById('dbDisplay');
        const canvas = document.getElementById('oscilloscope');
        const canvasCtx = canvas.getContext('2d');
        const metaDevice = document.getElementById('metaDevice');
        const metaActive = document.getElementById('metaActive');
        const metaReady = document.getElementById('metaReady');
        const metaFormat = document.getElementById('metaFormat');
        const logBox = document.getElementById('logBox');

        function log(msg, type = 'info') {
            const el = document.createElement('div');
            el.className = 'log-entry ' + type;
            const now = new Date().toLocaleTimeString();
            el.textContent = `[${now}] ${msg}`;
            logBox.prepend(el);
        }

        async function enumerateAudioDevices() {
            try {
                const devices = await navigator.mediaDevices.enumerateDevices();
                const audioInputs = devices.filter(d => d.kind === 'audioinput');
                
                cboDevices.innerHTML = '<option value="""">Auto (Windows Default Recording Device)</option>';
                audioInputs.forEach((dev, idx) => {
                    const opt = document.createElement('option');
                    opt.value = dev.deviceId;
                    opt.textContent = dev.label || `Microphone ${idx + 1} (${dev.deviceId.substring(0, 8)}...)`;
                    cboDevices.appendChild(opt);
                });
                log(`Enumerated ${audioInputs.length} audio input device(s).`, 'info');
            } catch (err) {
                log(`Device enumeration error: ${err.message}`, 'error');
            }
        }

        async function startMicrophone() {
            if (mediaStream) stopMicrophone();

            const selectedDeviceId = cboDevices.value;
            const cleanAudio = chkCleanAudio.checked;

            const constraints = {
                audio: selectedDeviceId ? { deviceId: { exact: selectedDeviceId } } : true
            };

            if (cleanAudio && typeof constraints.audio === 'object') {
                constraints.audio.echoCancellation = false;
                constraints.audio.noiseSuppression = false;
                constraints.audio.autoGainControl = false;
            }

            try {
                log(`Requesting getUserMedia...`, 'info');
                mediaStream = await navigator.mediaDevices.getUserMedia(constraints);
                
                const track = mediaStream.getAudioTracks()[0];
                if (!track) {
                    throw new Error(""No audio tracks returned in media stream."");
                }

                // Update UI status
                statusPill.className = 'status-pill status-active';
                statusText.innerHTML = '<span class=""dot active-dot""></span> Active (Capturing)';
                btnStart.disabled = true;
                btnStop.disabled = false;
                startTime = Date.now();

                timerInterval = setInterval(() => {
                    const elapsedSec = Math.floor((Date.now() - startTime) / 1000);
                    const mins = String(Math.floor(elapsedSec / 60)).padStart(2, '0');
                    const secs = String(elapsedSec % 60).padStart(2, '0');
                    timerText.textContent = `Streaming: ${mins}:${secs}`;
                }, 1000);

                // Populate telemetry
                metaDevice.textContent = track.label || 'Default Microphone';
                metaActive.textContent = `${mediaStream.active} / Enabled: ${track.enabled}`;
                metaReady.textContent = track.readyState;

                const settings = track.getSettings ? track.getSettings() : {};
                metaFormat.textContent = `${settings.sampleRate || 48000} Hz, ${settings.channelCount || 2} ch`;

                log(`Stream acquired: ${track.label}`, 'success');

                // Track events
                track.onended = () => {
                    log(`Track ended event fired!`, 'warn');
                    stopMicrophone();
                };
                track.onmute = () => log(`Track muted by system/browser.`, 'warn');
                track.onunmute = () => log(`Track unmuted.`, 'success');

                // Set up Web Audio API pipeline for VU meter & oscilloscope
                audioContext = new (window.AudioContext || window.webkitAudioContext)();
                sourceNode = audioContext.createMediaStreamSource(mediaStream);
                analyserNode = audioContext.createAnalyser();
                analyserNode.fftSize = 2048;
                analyserNode.smoothingTimeConstant = 0.8;
                sourceNode.connect(analyserNode);

                drawVisualizer();
                await enumerateAudioDevices();
            } catch (err) {
                log(`getUserMedia failed: ${err.name} - ${err.message}`, 'error');
                statusPill.className = 'status-pill status-inactive';
                statusText.textContent = 'Error';
            }
        }

        function stopMicrophone() {
            if (animFrameId) {
                cancelAnimationFrame(animFrameId);
                animFrameId = null;
            }
            if (timerInterval) {
                clearInterval(timerInterval);
                timerInterval = null;
            }
            if (mediaStream) {
                mediaStream.getTracks().forEach(t => t.stop());
                mediaStream = null;
            }
            if (audioContext) {
                audioContext.close();
                audioContext = null;
            }

            statusPill.className = 'status-pill status-inactive';
            statusText.textContent = 'Inactive';
            btnStart.disabled = false;
            btnStop.disabled = true;
            timerText.textContent = 'Streaming: 00:00';
            vuFill.style.width = '0%';
            vuPeak.style.left = '0%';
            dbDisplay.textContent = '-∞ dB';
            metaActive.textContent = 'False';
            metaReady.textContent = 'ended';

            clearCanvas();
            log('Microphone test stopped.', 'info');
        }

        function clearCanvas() {
            canvas.width = canvas.clientWidth * window.devicePixelRatio;
            canvas.height = canvas.clientHeight * window.devicePixelRatio;
            canvasCtx.fillStyle = '#0b0b0f';
            canvasCtx.fillRect(0, 0, canvas.width, canvas.height);
            canvasCtx.lineWidth = 1.5;
            canvasCtx.strokeStyle = '#2e2e3d';
            canvasCtx.beginPath();
            canvasCtx.moveTo(0, canvas.height / 2);
            canvasCtx.lineTo(canvas.width, canvas.height / 2);
            canvasCtx.stroke();
        }

        function drawVisualizer() {
            if (!analyserNode) return;

            const bufferLength = analyserNode.fftSize;
            const dataArray = new Uint8Array(bufferLength);
            analyserNode.getByteTimeDomainData(dataArray);

            // Calculate RMS level
            let sumSquares = 0;
            for (let i = 0; i < bufferLength; i++) {
                const norm = (dataArray[i] - 128) / 128;
                sumSquares += norm * norm;
            }
            const rms = Math.sqrt(sumSquares / bufferLength);
            const db = 20 * Math.log10(Math.max(rms, 0.0001));

            // Map dB [-60dB .. 0dB] to percentage [0% .. 100%]
            const pct = Math.max(0, Math.min(100, ((db + 60) / 60) * 100));
            vuFill.style.width = `${pct}%`;

            if (pct > peakLevel) {
                peakLevel = pct;
            } else {
                peakLevel = Math.max(0, peakLevel - 0.75); // Slow decay
            }
            vuPeak.style.left = `${peakLevel}%`;
            dbDisplay.textContent = rms < 0.001 ? '-∞ dB' : `${db.toFixed(1)} dB`;

            // Draw oscilloscope
            canvas.width = canvas.clientWidth * window.devicePixelRatio;
            canvas.height = canvas.clientHeight * window.devicePixelRatio;
            canvasCtx.fillStyle = '#0b0b0f';
            canvasCtx.fillRect(0, 0, canvas.width, canvas.height);

            // Grid lines
            canvasCtx.lineWidth = 1;
            canvasCtx.strokeStyle = '#1e1e28';
            canvasCtx.beginPath();
            canvasCtx.moveTo(0, canvas.height / 2);
            canvasCtx.lineTo(canvas.width, canvas.height / 2);
            canvasCtx.stroke();

            // Waveform line
            canvasCtx.lineWidth = 2.5 * window.devicePixelRatio;
            canvasCtx.strokeStyle = pct > 75 ? '#f59e0b' : '#10b981';
            canvasCtx.beginPath();

            const sliceWidth = canvas.width / bufferLength;
            let x = 0;

            for (let i = 0; i < bufferLength; i++) {
                const v = dataArray[i] / 128.0;
                const y = (v * canvas.height) / 2;

                if (i === 0) canvasCtx.moveTo(x, y);
                else canvasCtx.lineTo(x, y);

                x += sliceWidth;
            }

            canvasCtx.stroke();
            animFrameId = requestAnimationFrame(drawVisualizer);
        }

        btnStart.addEventListener('click', startMicrophone);
        btnStop.addEventListener('click', stopMicrophone);
        cboDevices.addEventListener('change', () => {
            if (mediaStream) startMicrophone();
        });
        chkCleanAudio.addEventListener('change', () => {
            if (mediaStream) startMicrophone();
        });

        window.addEventListener('resize', clearCanvas);
        clearCanvas();
        enumerateAudioDevices();
    </script>
</body>
</html>";
    }
}
