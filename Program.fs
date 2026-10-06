#nowarn "9"
module BadAppleFSharp

open System
open System.IO
open System.Threading
open System.Diagnostics
open System.Runtime.InteropServices
open OpenCvSharp
open NAudio.Wave

[<DllImport("kernel32.dll")>]
extern bool GetConsoleMode(nativeint hConsoleHandle, uint32& lpMode)

[<DllImport("kernel32.dll")>]
extern bool SetConsoleMode(nativeint hConsoleHandle, uint32 dwMode)

[<DllImport("kernel32.dll")>]
extern nativeint GetStdHandle(int nStdHandle)

let enableAnsi () =
    let STD_OUTPUT_HANDLE = -11
    let ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004u
    let handle = GetStdHandle(STD_OUTPUT_HANDLE)
    let mutable mode = 0u
    if GetConsoleMode(handle, &mode) then
        SetConsoleMode(handle, mode ||| ENABLE_VIRTUAL_TERMINAL_PROCESSING) |> ignore

let asciiBytes =
    "@#S%?*+;:, "
    |> Seq.map byte
    |> Seq.toArray

let mutable frameWidth = 150
let fps = 30.0
let frameDelayTicks = int64 (float Stopwatch.Frequency / fps)

let frameToBytes (frame: Mat) (width: int) : byte[] =
    let aspectRatio = float frame.Rows / (float frame.Cols * 2.5)
    let height = max 1 (int (aspectRatio * float width))

    use resized = new Mat()
    Cv2.Resize(frame, resized, Size(width, height))

    use gray = new Mat()
    Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY)

    let rows = gray.Rows
    let cols = gray.Cols
    let bufSize = rows * cols + (rows - 1)
    let buf = Array.zeroCreate<byte> bufSize

    let step = int (gray.Step())
    let pixelData = Array.zeroCreate<byte> (rows * step)
    Marshal.Copy(NativeInterop.NativePtr.toNativeInt gray.DataPointer, pixelData, 0, pixelData.Length)

    let mutable pos = 0
    for row in 0 .. rows - 1 do
        let rowOffset = row * step
        for col in 0 .. cols - 1 do
            let brightness = pixelData.[rowOffset + col]
            buf.[pos] <- asciiBytes.[min (int brightness / 25) 10]
            pos <- pos + 1
        if row < rows - 1 then
            buf.[pos] <- byte '\n'
            pos <- pos + 1
    buf

let progressBar (current: int) (total: int) =
    let pct = float current * 100.0 / float total
    let filled = max 0 (int (pct / 4.0) - 1)
    let arrow  = String.replicate filled "#"
    let spaces = String.replicate (25 - filled) " "
    Console.Write($"\rProgress: [{arrow}{spaces}] {int pct}%% Frame {current} of {total} frames")

let generateAsciiFrames (videoPath: string) : byte[][] =
    use cap = new VideoCapture(videoPath)
    let totalFrames = int (cap.Get(VideoCaptureProperties.FrameCount))

    printfn "Beginning ASCII generation..."
    printfn "Total frames: %d | Width: %d | FPS: %.1f" totalFrames frameWidth fps

    let frames = System.Collections.Generic.List<byte[]>(totalFrames)
    let sw = Stopwatch.StartNew()

    use frame = new Mat()
    let mutable count = 0

    while cap.Read(frame) do
        if not (frame.Empty()) then
            frames.Add(frameToBytes frame frameWidth)
            count <- count + 1
            if count % 100 = 0 then progressBar count totalFrames

    sw.Stop()
    Console.WriteLine()
    printfn "\nASCII generation completed in %.2f seconds." sw.Elapsed.TotalSeconds
    frames.ToArray()

let playAudio (path: string) (cts: CancellationTokenSource) =
    let t = Thread(fun () ->
        try
            use reader  = new Mp3FileReader(path)
            use waveOut = new WaveOut()
            waveOut.Init(reader)
            waveOut.Play()
            while waveOut.PlaybackState = PlaybackState.Playing
                  && not cts.Token.IsCancellationRequested do
                Thread.Sleep(100)
        with ex -> eprintfn "\n[Audio Error] %s" ex.Message
    )
    t.IsBackground <- true
    t.Priority <- ThreadPriority.AboveNormal
    t.Start()
    t
let ansiHome = "\x1b[H"B

let playVideo (frames: byte[][]) (startFrame: int) =
    try
        Console.BackgroundColor <- ConsoleColor.White
        Console.ForegroundColor <- ConsoleColor.Black
        Console.Clear()
        Console.CursorVisible <- false
    with _ -> ()

    let stdout = new BufferedStream(Console.OpenStandardOutput(), 1 <<< 20)

    let sw = Stopwatch.StartNew()
    let mutable nextTick = sw.ElapsedTicks

    for i in startFrame .. frames.Length - 1 do
        stdout.Write(ansiHome, 0, ansiHome.Length)
        stdout.Write(frames.[i], 0, frames.[i].Length)
        stdout.Flush()

        nextTick <- nextTick + frameDelayTicks
        let wakeEarlyTicks = int64 (float Stopwatch.Frequency * 0.001)
        let sleepMs = int ((nextTick - wakeEarlyTicks - sw.ElapsedTicks) * 1000L / Stopwatch.Frequency)
        if sleepMs > 1 then Thread.Sleep(sleepMs)
        while sw.ElapsedTicks < nextTick do ()

    stdout.Close()

    try
        Console.CursorVisible <- true
        Console.ResetColor()
        Console.Clear()
    with _ -> ()

[<EntryPoint>]
let main _ =
    enableAnsi ()

    let videoPath = "source/BadApple.mp4"
    let mp3Path   = "source/bad-apple-audio.mp3"
    let midiPath  = "source/alstroemeria_records_bad_apple.mid"

    if not (File.Exists(videoPath)) then
        eprintfn "ERROR: Video file not found at '%s'" videoPath
        eprintfn "Please make sure BadApple.mp4 is in the source/ folder."
        1
    else

    let mutable asciiFrames: byte[][] = [||]
    let mutable running = true

    while running do
        printfn ""
        printfn "=============================================================="
        printfn "  Bad Apple!!"
        printfn "=============================================================="
        printfn "  1) Play  (MP3 audio)"
        printfn "  2) Play  (MIDI audio — starts 30 frames in for sync)"
        printfn "  3) Set frame width (current: %d)" frameWidth
        printfn "  4) Exit"
        printfn "=============================================================="
        printf "Your option: "

        let input = Console.ReadLine().Trim()

        match input with
        | "1" | "2" ->
            if asciiFrames.Length = 0 then
                asciiFrames <- generateAsciiFrames videoPath
                Thread.Sleep(1000)

            let cts = new CancellationTokenSource()
            let audioPath  = if input = "1" then mp3Path else midiPath
            let startFrame = if input = "2" then 30 else 0

            if File.Exists(audioPath) then
                let _t = playAudio audioPath cts
                Thread.Sleep(50)
                playVideo asciiFrames startFrame
                cts.Cancel()
            else
                printfn "[Warning] Audio file not found: %s" audioPath
                playVideo asciiFrames startFrame

        | "3" ->
            printf "Enter frame width (e.g. 100, 150, 200): "
            match Int32.TryParse(Console.ReadLine().Trim()) with
            | true, w when w > 0 ->
                frameWidth <- w
                asciiFrames <- [||]
                printfn "Frame width set to %d. ASCII frames will regenerate on next play." w
            | _ ->
                printfn "Invalid input. Width unchanged."

        | "4" | "q" | "quit" | "exit" ->
            running <- false

        | _ ->
            printfn "Unknown option!"

    0
