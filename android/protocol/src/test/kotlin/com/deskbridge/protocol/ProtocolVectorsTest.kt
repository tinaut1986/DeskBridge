package com.deskbridge.protocol

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Checks this implementation against protocol-vectors.json, written by the C# test ProtocolVectorTests from the
 * real Windows implementation: same proofs, keys, JSON and encrypted bytes.
 */
class ProtocolVectorsTest {
    private val v: JsonObject = ProtocolJson.parseToJsonElement(
        javaClass.getResource("/protocol-vectors.json")!!.readText()).jsonObject

    private fun s(name: String) = v[name]!!.jsonPrimitive.content
    private fun msg(name: String) = v["messages"]!!.jsonObject[name]!!.jsonPrimitive.content
    private fun hex(text: String) = text.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    private fun toHex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it) }

    private val key get() = ControlAuth.decodeKey(s("key"))

    @Test
    fun protocol_version_matches() = assertEquals(ControlAuth.PROTOCOL_VERSION, v["protocolVersion"]!!.jsonPrimitive.int)

    @Test
    fun proofs_and_session_keys_match() {
        assertEquals(s("clientProof"), ControlAuth.clientProof(key, s("serverNonce"), s("clientNonce"), s("client"), s("server")))
        assertEquals(s("serverProof"), ControlAuth.serverProof(key, s("serverNonce"), s("clientNonce"), s("client"), s("server")))
        val (c2s, s2c) = ControlAuth.sessionKeys(key, s("serverNonce"), s("clientNonce"))
        assertEquals(s("clientToServer"), toHex(c2s))
        assertEquals(s("serverToClient"), toHex(s2c))
    }

    @Test
    fun encrypted_lines_decrypt_and_encrypt_to_the_same_bytes() {
        val lines = v["serverLines"]!!.jsonArray.map { it.jsonPrimitive.content }
        val jsons = v["serverLinesJson"]!!.jsonArray.map { it.jsonPrimitive.content }

        // The tablet reads what the coordinator sent...
        val input = ByteArrayInputStream(lines.joinToString("") { "$it\n" }.toByteArray(Charsets.US_ASCII))
        val out = ByteArrayOutputStream()
        val tablet = LineChannel(input, out)
        tablet.encrypt(sendKey = hex(s("clientToServer")), receiveKey = hex(s("serverToClient")))
        assertEquals(jsons[0], tablet.receiveJson())
        assertEquals(jsons[1], tablet.receiveJson())
        assertNull(tablet.receiveJson())

        // ...and its reply is byte for byte what C# would send.
        tablet.sendJson(s("clientLineJson"))
        assertEquals(s("clientLine") + "\n", out.toString(Charsets.US_ASCII))
    }

    @Test
    fun messages_from_the_coordinator_parse() {
        fun parse(name: String) = ProtocolJson.decodeFromString(ControlMessage.serializer(), msg(name))
        val challenge = assertIs<ChallengeMessage>(parse("challenge"))
        assertEquals("desktop", challenge.machine)
        assertIs<WelcomeMessage>(parse("welcome"))
        assertEquals("no", assertIs<ErrorMessage>(parse("error")).message)
        val start = assertIs<StartVideoMessage>(parse("startVideo"))
        assertEquals(StartVideoMessage("desktop/v1->tablet/1", false, "1", 60, 20), start)
        assertNull(assertIs<StopVideoMessage>(parse("stopVideo")).key)
        val events = assertIs<InputEventsMessage>(parse("inputEvents"))
        assertEquals(listOf(InputEvent(InputKind.MouseMove, 3, -2), InputEvent(InputKind.Key, code = 0x1E, down = true)), events.events)
        assertEquals(9L, events.seq)
        val enter = assertIs<InputEnterMessage>(parse("inputEnter"))
        assertEquals(PlacementSide.Below, enter.side)
        assertEquals(10, enter.x)
    }

    @Test
    fun messages_from_the_tablet_encode_as_in_csharp() {
        fun same(name: String, message: ControlMessage) = assertEquals(
            ProtocolJson.parseToJsonElement(msg(name)),
            ProtocolJson.encodeToJsonElement(ControlMessage.serializer(), message), name)

        same("auth", AuthMessage(s("client"), s("clientNonce"), s("clientProof"), link = true))
        same("authVideo", AuthMessage(s("client"), s("clientNonce"), "p", video = "desktop/v1->tablet/1"))
        same("applied", AppliedMessage("tablet", "personal", listOf("w1")))
        same("videoStarted", VideoStartedMessage(0, 0, 2560, 1600))
        same("diagnostics", DiagnosticsMessage("tablet", "0.1.0", "desktop:47900", 47900, false, null, listOf("line")))
        same("inputLeave", InputLeaveMessage(0.25, 640, 0))
        same("inputCursor", InputCursorMessage(9, 100, 200))
    }

    @Test
    fun video_packets_from_the_coordinator_decode() {
        val (c2s, s2c) = ControlAuth.sessionKeys(key, s("serverNonce"), s("clientNonce"))
        val channel = VideoChannel.overStreams(ByteArrayInputStream(hex(s("videoServerBytes"))), ByteArrayOutputStream(), c2s, s2c)

        val (t1, format) = channel.receive()!!
        assertEquals(VideoPacket.Format, t1)
        assertEquals(VideoFormat(2560, 1600, 60, 20, "GPU", "Encoder"), VideoFormat.parse(format))
        assertEquals(s("videoFormatJson"), String(VideoFormat.encode(VideoFormat.parse(format))))

        val (t2, frameBody) = channel.receive()!!
        assertEquals(VideoPacket.Frame, t2)
        val frame = VideoBodies.parseFrame(frameBody)
        assertEquals(1_234_567L, frame.time)
        assertTrue(frame.keyFrame)
        assertContentEquals(hex(s("videoFrame")), frame.data.copyOfRange(frame.offset, frame.data.size))

        val (t3, pointer) = channel.receive()!!
        assertEquals(VideoPacket.Pointer, t3)
        assertEquals(VideoBodies.Pointer(-5, 300, true), VideoBodies.parsePointer(pointer))

        val (t4, shapeBody) = channel.receive()!!
        assertEquals(VideoPacket.PointerShape, t4)
        val shape = VideoBodies.parsePointerShape(shapeBody)!!
        assertEquals(listOf(1, 2, 0, 1), listOf(shape.width, shape.height, shape.hotX, shape.hotY))
        assertContentEquals(byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8), shape.bgra)

        assertNull(channel.receive())
    }

    @Test
    fun video_packets_the_tablet_sends_round_trip() {
        val (c2s, s2c) = ControlAuth.sessionKeys(key, s("serverNonce"), s("clientNonce"))
        val wire = ByteArrayOutputStream()
        val tablet = VideoChannel.overStreams(ByteArrayInputStream(ByteArray(0)), wire, c2s, s2c)
        tablet.send(VideoPacket.KeyFrameRequest)
        tablet.send(VideoPacket.Shown, VideoBodies.timeBody(42))
        val coordinator = VideoChannel.overStreams(ByteArrayInputStream(wire.toByteArray()), ByteArrayOutputStream(), s2c, c2s)
        assertEquals(VideoPacket.KeyFrameRequest, coordinator.receive()!!.first)
        val (type, body) = coordinator.receive()!!
        assertEquals(VideoPacket.Shown, type)
        assertContentEquals(VideoBodies.timeBody(42), body)
    }

    @Test
    fun addresses_parse_like_csharp() {
        assertEquals("192.168.1.10" to 47900, ControlClient.parseAddress("192.168.1.10"))
        assertEquals("desktop" to 5000, ControlClient.parseAddress(" desktop:5000 "))
        assertEquals("fe80::1" to 47900, ControlClient.parseAddress("fe80::1"))
    }
}
