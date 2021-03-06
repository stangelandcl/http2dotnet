using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

using Http2;
using Http2.Hpack;
using static Http2Tests.TestHeaders;

namespace Http2Tests
{
    public class FlowControlTests
    {
        private ILoggerProvider loggerProvider;

        private readonly HeaderField[] DefaultGetHeaders = new HeaderField[]
        {
            new HeaderField { Name = ":method", Value = "GET" },
            new HeaderField { Name = ":scheme", Value = "http" },
            new HeaderField { Name = ":path", Value = "/" },
        };

        public FlowControlTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        [Theory]
        [InlineData(new int[]{1}, new int[]{0}, new int[]{0})]
        [InlineData(new int[]{7999}, new int[]{0}, new int[]{0})]
        [InlineData(new int[]{7999,0}, new int[]{0,0}, new int[]{0,0})]
        [InlineData(new int[]{8001}, new int[]{0}, new int[]{0})]
        [InlineData(new int[]{8001,0}, new int[]{8001,0}, new int[]{0,0})]
        [InlineData(new int[]{16000}, new int[]{0}, new int[]{0})]
        [InlineData(new int[]{16000,0}, new int[]{16000,0}, new int[]{0,0})]
        [InlineData(new int[]{16000,16000}, new int[]{16000,0}, new int[]{0,0})]
        [InlineData(new int[]{16000,16000,0}, new int[]{16000,16000,0}, new int[]{0,0,0})]
        [InlineData(
            new int[]{16000,16000,2000},
            new int[]{16000,16000,0},
            new int[]{0,0,34000})]
        [InlineData(
            new int[]{16000,16000,2000,14000,16000,4000},
            new int[]{16000,16000,0,16000,16000,0},
            new int[]{0,0,34000,0,0,34000})]
        public async Task SendingLargeAmountOfDataShouldTriggerWindowUpdates(
            int[] dataLength,
            int[] expectedStreamWindowUpdates,
            int[] expectedConnWindowUpdates)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            // Lower the initial window size so that stream window updates are
            // sent earlier than connection window updates
            var settings = Settings.Default;
            settings.InitialWindowSize = 16000;
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe,
                localSettings: settings);

            // Consume all data on reader side
            var readTask = Task.Run(async () =>
            {
                await res.stream.ReadHeadersAsync();
                var data = await res.stream.ReadAllToArrayWithTimeout();
            });

            for (var i = 0; i < dataLength.Length; i++)
            {
                var toSend = dataLength[i];
                var isEndOfStream = i == (dataLength.Length - 1);
                await inPipe.WriteData(1u, toSend, endOfStream: isEndOfStream);
                // Wait for a short amount of time between DATA frames
                if (!isEndOfStream) await Task.Delay(5);
                // Check if window updates were received if required
                if (expectedConnWindowUpdates[i] != 0)
                {
                    await outPipe.AssertWindowUpdate(0, expectedConnWindowUpdates[i]);
                }
                if (expectedStreamWindowUpdates[i] != 0)
                {
                    await outPipe.AssertWindowUpdate(1, expectedStreamWindowUpdates[i]);
                }
            }
        }

        [Fact]
        public async Task StreamWindowUpdatesShouldRespectBufferState()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            // Lower the initial window size so that stream window updates are
            // sent earlier than connection window updates
            var settings = Settings.Default;
            settings.InitialWindowSize = 16000;
            settings.MaxFrameSize = 1000000;
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe,
                localSettings: settings);

            // Write 12k of data. Buffer amount: 12k. Remaining window: 4k
            await inPipe.WriteData(1u, 12000);
            // Read 5k of data. Buffer amount: 7k
            await res.stream.ReadAllWithTimeout(new ArraySegment<byte>(new byte[5000]));
            // This should not trigger a window update
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
            // Read 5k of data. Buffer amount: 2k
            await res.stream.ReadAllWithTimeout(new ArraySegment<byte>(new byte[5000]));
            // Expect a window update of 10k. Remaining window: 14k
            await outPipe.AssertWindowUpdate(1u, 10000);
            // Read 2k of data - buffer is now drained
            await res.stream.ReadAllWithTimeout(new ArraySegment<byte>(new byte[2000]));
            // This should not trigger a window update
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();

            // Write 4k of data. Buffer amount: 4k. Remaining window: 10k
            await inPipe.WriteData(1u, 4000);
            // Read 4k of data. Buffer amount: 0k
            await res.stream.ReadAllWithTimeout(new ArraySegment<byte>(new byte[4000]));
            // This should not trigger a window update
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();

            // Write 8k of data. Buffer amount: 8k. Remaining window: 2k
            await inPipe.WriteData(1u, 8000);
            // Read 5k of data. Buffer amount: 3k
            await res.stream.ReadAllWithTimeout(new ArraySegment<byte>(new byte[5000]));
            // Expect a window update of 11k. Remaining window: 13k
            await outPipe.AssertWindowUpdate(1u, 11000);
        }

        [Fact]
        public async Task WritesShouldRespectStreamFlowControlWindow()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            // Create a stream. It will have a flow control window of 64kb
            // for stream and connection
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);
            // Increase flow control window for connection so that write is blocked
            // on stream window
            await inPipe.WriteWindowUpdate(0, 1024*1024);
            var writeTask = Task.Run(async () =>
            {
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[32000]));
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[33535 + 1024]));
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[1024]));
            });
            // Expect to read the stream flow control window amount of data
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 15616);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 767);
            // Give a bigger flow control window for stream and expect more data
            await inPipe.WriteWindowUpdate(1u, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await inPipe.WriteWindowUpdate(1u, 1024);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await inPipe.WriteWindowUpdate(1u, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            // Expect the writer to finish
            var doneTask = await Task.WhenAny(writeTask, Task.Delay(250));
            Assert.True(writeTask == doneTask, "Expected write task to finish");
        }

        [Fact]
        public async Task WritesShouldRespectConnectionFlowControlWindow()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            // Create a stream. It will have a flow control window of 64kb
            // for stream and connection
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);
            // Increase flow control window for stream so that write is blocked
            // on connection window
            await inPipe.WriteWindowUpdate(1, 1024*1024);
            var writeTask = Task.Run(async () =>
            {
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[32000]));
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[33535+1024]));
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[1024]));
            });
            // Expect to read the stream flow control window amount of data
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 15616);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 767);
            // Give a bigger flow control window for connection and expect more data
            await inPipe.WriteWindowUpdate(0u, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await inPipe.WriteWindowUpdate(0u, 1024);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await inPipe.WriteWindowUpdate(0u, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            // Expect the writer to finish
            var doneTask = await Task.WhenAny(writeTask, Task.Delay(250));
            Assert.True(writeTask == doneTask, "Expected write task to finish");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SendingEmptyDataFramesShouldBePossibleWithoutFlowWindow(
            bool emptyFrameIsEndOfStream)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            // Create a stream. It will have a flow control window of 64kb
            // for stream and connection
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);
            var writeTask = Task.Run(async () =>
            {
                // Consume the complete flow control window
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[65535]));
                // And try to send an empty data frame
                await res.stream.WriteAsync(
                    new ArraySegment<byte>(new byte[0]), emptyFrameIsEndOfStream);
            });
            // Expect to read the sent frames
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 16383);
            await outPipe.ReadAndDiscardData(1u, emptyFrameIsEndOfStream, 0);
            // Expect the writer to finish
            var doneTask = await Task.WhenAny(writeTask, Task.Delay(250));
            Assert.True(writeTask == doneTask, "Expected write task to finish");
        }

        [Fact]
        public async Task BlockedWritesShouldUnblockOnExternalStreamReset()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            // Create a stream. It will have a flow control window of 64kb
            // for stream and connection
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);
            // Start discarding all ougoing data -> Not needed for this test
            // and pipe may not be blocked
            var readTask = Task.Run(async () =>
            {
                await outPipe.ReadAllToArrayWithTimeout();
            });
            // Write flow control window amount of data
            await res.stream.WriteWithTimeout(new ArraySegment<byte>(new byte[65535]));
            var resetTask = Task.Run(async () =>
            {
                await Task.Delay(20);
                await inPipe.WriteResetStream(1u, ErrorCode.Cancel);
            });
            // Write additional bytes. This should block and cause a streamreset
            // exception when the cancel arrives
            await Assert.ThrowsAsync<StreamResetException>(async () =>
            {
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[1024]));
            });
        }

        [Fact]
        public async Task BlockedWritesShouldUnblockOnStreamCancel()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            // Create a stream. It will have a flow control window of 64kb
            // for stream and connection
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);
            // Start discarding all ougoing data -> Not needed for this test
            // and pipe may not be blocked
            var readTask = Task.Run(async () =>
            {
                await outPipe.ReadAllToArrayWithTimeout();
            });
            // Write flow control window amount of data
            await res.stream.WriteWithTimeout(new ArraySegment<byte>(new byte[65535]));
            var resetTask = Task.Run(async () =>
            {
                await Task.Delay(20);
                res.stream.Cancel();
            });
            // Write additional bytes. This should block and cause a streamreset
            // exception when the cancel arrives
            await Assert.ThrowsAsync<StreamResetException>(async () =>
            {
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[1024]));
            });
        }

        [Fact]
        public async Task NegativeFlowControlWindowsThroughSettingsShouldBeSupported()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            // Start with a stream window of 10
            var settings = Settings.Default;
            settings.InitialWindowSize = 10;
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe,
                remoteSettings: settings);

            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);

            // Start to write 20 bytes of data
            var writeTask = Task.Run(async () =>
            {
                var data = new byte[20];
                await res.stream.WriteAsync(new ArraySegment<byte>(data), true);
            });

            // Expect to receive the first 10 bytes
            await outPipe.ReadAndDiscardData(1u, false, 10);

            // Stream has now a window of 0
            // Decrease that to -10 by decreasing the initial window
            settings.InitialWindowSize = 0;
            await inPipe.WriteSettings(settings);
            await outPipe.AssertSettingsAck();

            // Increase to 0 with window update
            await inPipe.WriteWindowUpdate(1u, 10);

            // Check that we get no data so far by ping/pong
            // If the negative window is not applied we would get new data here
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();

            // Increase to 10 with window update
            await inPipe.WriteWindowUpdate(1u, 10);

            // Expect the remaining 10 bytes of data
            await outPipe.ReadAndDiscardData(1u, true, 10);

            Assert.Equal(StreamState.HalfClosedLocal, res.stream.State);
        }

        [Fact]
        public async Task IncreasingFlowControlWindowsThroughSettingsShouldBeSupported()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            // Start with a stream window of 10
            var settings = Settings.Default;
            settings.InitialWindowSize = 10;
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe,
                remoteSettings: settings);

            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);

            // Start to write 20 bytes of data
            var writeTask = Task.Run(async () =>
            {
                var data = new byte[20];
                await res.stream.WriteAsync(new ArraySegment<byte>(data), true);
            });

            // Expect to receive the first 10 bytes
            await outPipe.ReadAndDiscardData(1u, false, 10);

            // Stream has now a window of 0
            // Increase to 10 by setting initial window to 20
            settings.InitialWindowSize = 20;
            await inPipe.WriteSettings(settings);
            await outPipe.AssertSettingsAck();

            // Expect the remaining 10 bytes of data
            await outPipe.ReadAndDiscardData(1u, true, 10);

            Assert.Equal(StreamState.HalfClosedLocal, res.stream.State);
        }

        [Fact]
        public async Task ShouldGoAwayWhenStreamWindowIsOverflowedThroughSettingsUpdate()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            // Let the remote increase the flow control window of the stream
            await inPipe.WriteWindowUpdate(
                1u,
                (int)(int.MaxValue - Settings.Default.InitialWindowSize));

            // Should be still alive
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();

            // And now write new settings which overflow the window
            var newSettings = Settings.Default;
            newSettings.InitialWindowSize++;
            await inPipe.WriteSettings(newSettings);

            // Dead through overflow
            await outPipe.AssertGoAwayReception(ErrorCode.FlowControlError, 1u);
        }

        [Fact]
        public async Task ShouldAllowToSetTheMaxPossibleConnectionFlowControlWindowSize()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, null);

            var amount = int.MaxValue - 65535;
            await inPipe.WriteWindowUpdate(0, amount);
            // Check aliveness with ping/pong
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
        }

        [Theory]
        [InlineData(int.MaxValue - 65535 + 1)]
        [InlineData(int.MaxValue)]
        public async Task ShouldGoAwayWhenConnectionFlowControlWindowIsOverloaded(
            int amount)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, null);

            await inPipe.WriteWindowUpdate(0, amount);
            await outPipe.AssertGoAwayReception(ErrorCode.FlowControlError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task ShouldAllowToSetTheMaxPossibleStreamFlowControlWindowSize()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, DefaultGetHeaders);

            var amount = int.MaxValue - 65535;
            await inPipe.WriteWindowUpdate(1, amount);
            // Check aliveness with ping/pong
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
        }

        [Theory]
        [InlineData(int.MaxValue - 65535 + 1)]
        [InlineData(int.MaxValue)]
        public async Task ShouldResetStreamWhenStreamFlowControlWindowIsOverloaded(
            int amount)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, DefaultGetHeaders);

            await inPipe.WriteWindowUpdate(1, amount);
            await outPipe.AssertResetStreamReception(1, ErrorCode.FlowControlError);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public async Task ReceivingWindowUpdatesWith0AmountShouldTriggerGoAwayOrReset(
            uint streamId)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            await inPipe.WriteWindowUpdate(streamId, 0);
            if (streamId == 0)
            {
                await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 1);
                await outPipe.AssertStreamEnd();
            }
            else
            {
                await outPipe.AssertResetStreamReception(1u, ErrorCode.ProtocolError);
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public async Task ReceivingWindowUpdatesOnIdleStreamsShouldTriggerGoAway(
            uint streamId)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            await inPipe.WriteWindowUpdate(streamId, 0);
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 1);
            await outPipe.AssertStreamEnd();
        }

        [Theory]
        [InlineData(null, 1u, 65535, false)]
        [InlineData(null, 1u, 65536, true)]
        [InlineData(0, 1u, 65535, false)]
        [InlineData(0, 1u, 65536, true)]
        [InlineData(1, 1u, 65535, false)]
        [InlineData(1, 1u, 65536, true)]
        [InlineData(255, 1u, 65535, false)]
        [InlineData(255, 1u, 65536, true)]
        // Check the same properties on an unknown stream with ID 3
        [InlineData(null, 3u, 65535, false)] // TODO: FIXME
        [InlineData(null, 3u, 65536, true)]
        [InlineData(0, 3u, 65535, false)]
        [InlineData(0, 3u, 65536, true)]
        [InlineData(1, 3u, 65535, false)]
        [InlineData(1, 3u, 65536, true)]
        [InlineData(255, 3u, 65535, false)]
        [InlineData(255, 3u, 65536, true)]
        public async Task ViolationsOfTheConnectionFlowControlWindowShouldBeDetected(
            int? padLen, uint streamId, int dataAmount, bool isError)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            // Use a bigger flow control window for streams so that the connection
            // window errors and streams dont send window updates.
            // Also update maxFrameSize, otherwise the connection will send
            // window updates faster than we can violate the contract
            var settings = Settings.Default;
            settings.InitialWindowSize = 256*1024;
            settings.MaxFrameSize = 1024*1024;

            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe,
                localSettings: settings);

            // Open an additional stream, so that streamId 3 is not in the IDLE
            // range, which causes a connection error
            await inPipe.WriteHeaders(
                res.hEncoder, 555u, false, DefaultGetHeaders);

            // Try to send the data
            // This might fail, if the connection goes away before
            // everything is read
            try
            {
                await inPipe.WriteData(streamId, dataAmount, padLen: padLen);
            }
            catch (Exception e)
            {
                if (!isError || !(e is TimeoutException))
                    throw;
            }

            if (isError)
            {
                await outPipe.AssertGoAwayReception(ErrorCode.FlowControlError, 555u);
                await outPipe.AssertStreamEnd();
                await res.conn.Done;
                Assert.Equal(StreamState.Reset, res.stream.State);
            }
            else
            {
                // Expect the connection to be alive
                await outPipe.AssertWindowUpdate(0u, 65535);
                if (streamId != 1)
                {
                    // We expect a reset for the unknown stream on which data
                    // was transmitted
                    await outPipe.AssertResetStreamReception(streamId, ErrorCode.StreamClosed);
                }
                await inPipe.WritePing(new byte[8], false);
                await outPipe.ReadAndDiscardPong();
                Assert.Equal(StreamState.Open, res.stream.State);
            }
        }

        [Theory]
        [InlineData(null, 16000, 16000, false)]
        [InlineData(null, 16000, 16001, true)]
        [InlineData(0, 16000, 16000, false)]
        [InlineData(0, 16000, 16001, true)]
        [InlineData(1, 16000, 16000, false)]
        [InlineData(1, 16000, 16001, true)]
        [InlineData(255, 16000, 16000, false)]
        [InlineData(255, 16000, 16001, true)]
        public async Task ViolationsOfTheStreamFlowControlWindowShouldBeDetected(
            int? padLen, int streamWindowSize, int dataAmount, bool isError)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            // Use a smaller flow control window for streams so that the stream
            // window errors.
            var settings = Settings.Default;
            settings.InitialWindowSize = (uint)streamWindowSize;
            settings.MaxFrameSize = 1024*1024;
            // The test values for the amount of data to be sent on the stream
            // are tuned small enough that no connection window
            // updates will be sent, which would fail the expected output.

            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe,
                localSettings: settings);

            // Write more data than the stream allows
            await inPipe.WriteData(1u, dataAmount, padLen: padLen);

            if (isError)
            {
                // Expect that the stream got reset
                await outPipe.AssertResetStreamReception(1u, ErrorCode.FlowControlError);
                Assert.Equal(StreamState.Reset, res.stream.State);
            }

            // Send a ping afterwards, which should be processed in all cases
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ReceivingEmptyDataFramesShouldBePossibleIfStreamWindowIsDrained(
            bool emptyFrameIsEndOfStream)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var localSettings = Settings.Default;
            localSettings.InitialWindowSize = 10;
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe,
                localSettings: localSettings);

            // Consume the complete stream flow control window
            await inPipe.WriteData(1u, 10);

            // Send a 0byte data frame
            await inPipe.WriteData(1u, 0, endOfStream: emptyFrameIsEndOfStream);

            // Check if we are still alive
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
        }
    }
}