﻿// Copyright (c) 2007-2018 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu-framework/master/LICENCE

using osu.Framework.Allocation;
using osu.Framework.Graphics.OpenGL.Textures;
using osu.Framework.Graphics.Textures;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Represents a video decoder that can be used convert video streams and files into textures.
    /// </summary>
    public unsafe class VideoDecoder : IDisposable
    {
        private bool isDisposed;

        private Stream videoStream;

        /// <summary>
        /// The duration of the video that is being decoded. Can only be queried after the decoder has started decoding has loaded. This value may be an estimate by FFmpeg, depending on the video loaded.
        /// </summary>
        public double Duration => formatContext->duration / AVUtil.AV_TIME_BASE * 1000;

        /// <summary>
        /// True if the decoder currently does not decode any more frames, false otherwise.
        /// </summary>
        public bool IsPaused { get; set; }

        /// <summary>
        /// True if the decoder has faulted after starting to decode. You can try to restart a failed decoder by invoking <see cref="StartDecoding"/> again.
        /// </summary>
        public bool IsFaulted { get; private set; }

        /// <summary>
        /// The timestamp of the last frame that was decoded by this video decoder, or 0 if no frames have been decoded.
        /// </summary>
        public float LastDecodedFrameTime => lastDecodedFrameTime;

        /// <summary>
        /// The frame rate of the video stream this decoder is decoding.
        /// </summary>
        public double Framerate => AVUtil.av_q2d(stream->avg_frame_rate);

        /// <summary>
        /// True if the decoder can seek, false otherwise. Determined by the stream this decoder was created with.
        /// </summary>
        public bool CanSeek => videoStream.CanSeek;

        // libav-context-related
        private AVFormatContext* formatContext;
        private AVStream* stream;
        private AVCodecParameters codecParams;
        private byte* contextBuffer;
        private byte[] managedContextBuffer;
        private const int context_buffer_size = 4096;

        private AVFormat.ReadPacketCallback readPacketCallback;
        private AVFormat.SeekCallback seekCallback;

        private double timeBaseInSeconds;

        // frame data
        private AVFrame* frame;
        private AVFrame* frameRgb;
        private IntPtr frameRgbBufferPtr;
        private int uncompressedFrameSize;


        // active decoder state
        private volatile float lastDecodedFrameTime;

        private Thread decodingThread;

        private readonly ConcurrentQueue<DecodedFrame> decodedFrames;
        private readonly ConcurrentQueue<Action> decoderCommands;

        /// <summary>
        /// Creates a new video decoder that decodes the given video file.
        /// </summary>
        /// <param name="filename">The path to the file that should be decoded.</param>
        public VideoDecoder(string filename)
            : this(File.OpenRead(filename))
        {

        }

        /// <summary>
        /// Creates a new video decoder that decodes the given video stream.
        /// </summary>
        /// <param name="videoStream">The stream that should be decoded.</param>
        public VideoDecoder(Stream videoStream)
        {
            this.videoStream = videoStream;
            if (!videoStream.CanRead)
                throw new InvalidOperationException($"The given stream does not support reading. A stream used for a {nameof(VideoDecoder)} must support reading.");
            IsPaused = true;
            decodedFrames = new ConcurrentQueue<DecodedFrame>();
            decoderCommands = new ConcurrentQueue<Action>();
        }

        private int readPacket(void* opaque, byte* buf, int buf_size)
        {
            if (buf_size != managedContextBuffer.Length)
                managedContextBuffer = new byte[buf_size];

            var bytesRead = videoStream.Read(managedContextBuffer, 0, buf_size);
            Marshal.Copy(managedContextBuffer, 0, (IntPtr)buf, bytesRead);
            return bytesRead;
        }

        private long seek(void* opaque, long offset, int whence)
        {
            if (!videoStream.CanSeek)
                throw new InvalidOperationException("Tried seeking on a video sourced by a non-seekable stream.");

            switch (whence)
            {
                case StdIo.SEEK_CUR:
                    videoStream.Seek(offset, SeekOrigin.Current);
                    break;
                case StdIo.SEEK_END:
                    videoStream.Seek(offset, SeekOrigin.End);
                    break;
                case StdIo.SEEK_SET:
                    videoStream.Seek(offset, SeekOrigin.Begin);
                    break;
                case AVFormat.AVSEEK_SIZE:
                    return videoStream.Length;
                default:
                    return -1;
            }
            return videoStream.Position;
        }

        /// <summary>
        /// Seek the decoder to the given timestamp. This will fail if <see cref="CanSeek"/> is false.
        /// </summary>
        /// <param name="targetTimestamp">The timestamp to seek to.</param>
        public void Seek(double targetTimestamp)
        {
            if (!CanSeek)
                throw new InvalidOperationException("This decoder cannot seek because the underlying stream used to decode the video does not support seeking.");

            decoderCommands.Enqueue(() => AVFormat.av_seek_frame(formatContext, stream->index, (long)(targetTimestamp / timeBaseInSeconds / 1000.0), AVFormat.AVSEEK_FLAG_BACKWARD));
        }

        /// <summary>
        /// Decodes all frames in the video stream synchronously. This may take a long time and use a lot of memory.
        /// </summary>
        /// <returns>All decoded frames in the video.</returns>
        public IEnumerable<DecodedFrame> DecodeAllSynchronously()
        {
            prepareDecoding();
            decodingLoop(true);
            return GetDecodedFrames();
        }

        // sets up libavformat state: creates the AVFormatContext, the frames, etc. to start decoding, but does not actually start the decodingLoop
        private void prepareDecoding()
        {
            var fcPtr = AVFormat.avformat_alloc_context();
            formatContext = fcPtr;
            contextBuffer = AVUtil.av_malloc(context_buffer_size);
            managedContextBuffer = new byte[context_buffer_size];
            readPacketCallback = readPacket;
            seekCallback = seek;
            formatContext->pb = AVFormat.avio_alloc_context(contextBuffer, context_buffer_size, 0, null, readPacketCallback, IntPtr.Zero, seekCallback);
            if (AVFormat.avformat_open_input(&fcPtr, "dummy", IntPtr.Zero, IntPtr.Zero) < 0)
                throw new Exception("Error opening file.");

            if (AVFormat.avformat_find_stream_info((IntPtr)formatContext, IntPtr.Zero) < 0)
                throw new Exception("Could not find stream info.");

            var nStreams = formatContext->nb_streams;
            for (var i = 0; i < nStreams; ++i)
            {
                stream = formatContext->streams[i];

                codecParams = *stream->codecpar;
                if (codecParams.codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    timeBaseInSeconds = AVUtil.av_q2d(stream->time_base);
                    var codecPtr = AVCodec.avcodec_find_decoder(codecParams.codec_id);
                    if (codecPtr == IntPtr.Zero)
                        throw new Exception("Could not find codec.");


                    if (AVCodec.avcodec_open2(stream->codec, codecPtr, IntPtr.Zero) < 0)
                        throw new Exception("Could not open codec.");

                    frame = AVUtil.av_frame_alloc();
                    frameRgb = AVUtil.av_frame_alloc();

                    uncompressedFrameSize = AVUtil.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_RGBA, codecParams.width, codecParams.height);
                    frameRgbBufferPtr = Marshal.AllocHGlobal(uncompressedFrameSize);

                    var result = AVUtil.av_image_fill_arrays(&frameRgb->data0, &frameRgb->linesize[0], frameRgbBufferPtr, AVPixelFormat.AV_PIX_FMT_RGBA, codecParams.width, codecParams.height, 1);
                    if (result < 0)
                        throw new Exception("Could not fill image arrays");

                    break;
                }
            }
        }

        /// <summary>
        /// Starts the decoding process. The decoding will happen asynchronously in a separate thread. The decoded frames can be retrieved by using <see cref="GetDecodedFrames"/>.
        /// </summary>
        public void StartDecoding()
        {
            IsPaused = false;

            prepareDecoding();
            decodingThread = new Thread(decodingLoop)
            {
                IsBackground = true
            };
            decodingThread.Start();
        }

        /// <summary>
        /// Gets all frames that have been decoded by the decoder up until the point in time when this method was called. Retrieving decoded frames using this method consumes them, ie calling this method again will never retrieve the same frame twice.
        /// </summary>
        /// <returns>The frames that have been decoded up until the point in time this method was called.</returns>
        public IEnumerable<DecodedFrame> GetDecodedFrames()
        {
            var frames = new List<DecodedFrame>(decodedFrames.Count);
            while (decodedFrames.TryDequeue(out var df))
                frames.Add(df);

            return frames;
        }

        private void decodingLoop(object state)
        {
            var exitOnFirstReadFrameError = (state as bool?) ?? false;

            var packet = AVCodec.av_packet_alloc();
            // this should be massively reduced to something like 5-10, currently there is an issue with texture uploads not completing
            // in a predictable way though, which can cause huge overallocations. Going past the bufferstacks limit essentially breaks
            // video playback (~several GB memory usage building up very quickly accompanied by unacceptable framerates).
            var bufferStack = new BufferStack<byte>(300);
            IntPtr swsCtx = IntPtr.Zero;
            try
            {
                while (true)
                {
                    if (isDisposed)
                        return;

                    int readFrameResult = AVFormat.av_read_frame(formatContext, packet);
                    if (readFrameResult >= 0)
                    {
                        if (packet->stream_index == stream->index)
                        {
                            if (AVCodec.avcodec_send_packet(stream->codec, packet) < 0)
                                throw new Exception("Error sending packet.");

                            var result = AVCodec.avcodec_receive_frame(stream->codec, frame);
                            if (result == 0)
                            {
                                swsCtx = SWScale.sws_getContext(codecParams.width, codecParams.height, (AVPixelFormat)frame->format, codecParams.width, codecParams.height, AVPixelFormat.AV_PIX_FMT_RGBA, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                                SWScale.sws_scale(swsCtx, &frame->data0, &frame->linesize[0], 0, frame->height, &frameRgb->data0, &frameRgb->linesize[0]);
                                SWScale.sws_freeContext(swsCtx);
                                swsCtx = IntPtr.Zero;

                                var tex = new Texture(codecParams.width, codecParams.height, true);
                                var rawTex = new RawTexture(tex.Width, tex.Height, bufferStack);
                                Marshal.Copy((IntPtr)frameRgb->data0, rawTex.Data, 0, uncompressedFrameSize);
                                tex.SetData(new TextureUpload(rawTex));

                                var frameTime = (frame->best_effort_timestamp - stream->start_time) * timeBaseInSeconds;
                                decodedFrames.Enqueue(new DecodedFrame { Time = frameTime * 1000, Texture = tex });
                                lastDecodedFrameTime = (float)(frameTime * 1000f);
                            }
                        }
                    }

                    if (readFrameResult < 0 && exitOnFirstReadFrameError)
                        return;

                    while (!IsPaused || readFrameResult < 0)
                    {
                        if (isDisposed)
                            return;
                        // make sure we process misc commands even while idling
                        var executedCmd = false;
                        while (!decoderCommands.IsEmpty)
                        {
                            if (decoderCommands.TryDequeue(out var cmd))
                            {
                                cmd();
                                executedCmd = true;
                            }
                        }
                        if (executedCmd)
                            break;
                        Thread.Sleep(16);
                    }
                    while (!decoderCommands.IsEmpty)
                    {
                        if (isDisposed)
                            return;
                        if (decoderCommands.TryDequeue(out var cmd))
                            cmd();
                    }
                }
            }
            catch (Exception)
            {
                IsFaulted = true;
            }
            finally
            {
                AVCodec.av_packet_free(&packet);
                SWScale.sws_freeContext(swsCtx);
            }
        }

        #region Disposal

        ~VideoDecoder()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed)
                return;

            isDisposed = true;

            videoStream.Dispose();
            videoStream = null;

            while (decoderCommands.TryDequeue(out var _)) { }
            if (decodingThread != null)
            {
                // decodingThread checks isDisposed this and aborts if it's set, therefore .Join() should generally not cause a significant delay
                decodingThread.Join();
                decodingThread = null;
            }

            if (formatContext != null)
            {
                fixed (AVFormatContext** ptr = &formatContext)
                    AVFormat.avformat_close_input(ptr);
            }

            seekCallback = null;
            readPacketCallback = null;
            managedContextBuffer = null;

            // gets freed by libavformat when closing the input
            contextBuffer = null;

            if (frame != null)
            {
                fixed (AVFrame** ptr = &frame)
                    AVUtil.av_frame_free(ptr);
            }

            if (frameRgb != null)
            {
                fixed (AVFrame** ptr = &frameRgb)
                    AVUtil.av_frame_free(ptr);
            }

            if (frameRgbBufferPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(frameRgbBufferPtr);
                frameRgbBufferPtr = IntPtr.Zero;
            }

            while (decodedFrames.TryDequeue(out var f))
                f.Texture.Dispose();
        }

        #endregion
    }
}
