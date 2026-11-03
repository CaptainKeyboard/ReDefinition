using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Proxy;
using ReDefinition.Raytracing;
using UnityEngine;

namespace ReDefinition.Tests
{
    // What RtPacketBuilder writes into a frame's packet from the values it is handed. The ambient light and the
    // lights read Unity's colour space and settings, which need the game, so they are not here.
    [TestClass]
    public class RtPacketBuilderTests
    {
        // The rays' settings land in their slots of Params; the reach is the same for both kinds of ray.
        [TestMethod]
        public void ParamsFollowTheSettings()
        {
            RtNative.FramePacket packet = RtNative.NewFramePacket();
            var settings = new RtSettings { OcclusionDistance = 2f, Bounces = 3, Clamp = 20f };

            RtPacketBuilder.Params(ref packet, settings, true, false);

            CollectionAssert.AreEqual(new[] { 2f, 3f, 100000f, 100000f, 20f, 0f, 1f, 1f }, packet.Params);

            RtPacketBuilder.Params(ref packet, settings, false, true);

            Assert.AreEqual(1f, packet.Params[RtPacketBuilder.ParamEnvironment]);
            Assert.AreEqual(0f, packet.Params[RtPacketBuilder.ParamGBuffer]);
        }

        // The sun's direction is normalised and keeps the angular radius in w; its shadow strength is the colour's w.
        [TestMethod]
        public void TheSunIsADirectionAColourAndAShadowStrength()
        {
            RtNative.FramePacket packet = RtNative.NewFramePacket();

            RtPacketBuilder.Sun(ref packet, new Vector4(0f, 3f, 4f, 0.0047f), new Color(0.5f, 0.25f, 1f), 0.75f);

            Assert.AreEqual(0f, packet.SunDirection[0], 1e-6f);
            Assert.AreEqual(0.6f, packet.SunDirection[1], 1e-6f);
            Assert.AreEqual(0.8f, packet.SunDirection[2], 1e-6f);
            Assert.AreEqual(0.0047f, packet.SunDirection[3]);
            CollectionAssert.AreEqual(new[] { 0.5f, 0.25f, 1f, 0.75f }, packet.SunColor);
        }

        // Where the shadow does not fade, all six slots are 0 whatever the centre and the scale were.
        [TestMethod]
        public void NoFadeLeavesTheFadeSlotsZero()
        {
            RtNative.FramePacket packet = RtNative.NewFramePacket();
            for (int i = 0; i < 6; i++) packet.ShadowFade[i] = 9f;

            RtPacketBuilder.ShadowFade(ref packet, false, new Vector3(1f, 2f, 3f), 4f, new Vector2(5f, 6f));

            for (int i = 0; i < 6; i++) Assert.AreEqual(0f, packet.ShadowFade[i], "slot " + i);

            RtPacketBuilder.ShadowFade(ref packet, true, new Vector3(1f, 2f, 3f), 4f, new Vector2(5f, 6f));

            CollectionAssert.AreEqual(new[] { 1f, 2f, 3f, 4f, 5f, 6f },
                                      new[] { packet.ShadowFade[0], packet.ShadowFade[1], packet.ShadowFade[2],
                                              packet.ShadowFade[3], packet.ShadowFade[4], packet.ShadowFade[5] });
        }

        // The meadow's range and shade, then the vessel's offset and range, in the slots 6 to 11 of the shadow fade.
        [TestMethod]
        public void TheMeadowFillsTheTailOfTheShadowFade()
        {
            RtNative.FramePacket packet = RtNative.NewFramePacket();

            RtPacketBuilder.Meadow(ref packet, 320f, new Vector3(1f, -2f, 3f), 256f);

            CollectionAssert.AreEqual(new[] { 320f, RtPacketBuilder.MeadowShade, 1f, -2f, 3f, 256f },
                                      new[] { packet.ShadowFade[6], packet.ShadowFade[7], packet.ShadowFade[8],
                                              packet.ShadowFade[9], packet.ShadowFade[10], packet.ShadowFade[11] });
            Assert.AreEqual(0.82f, RtPacketBuilder.MeadowShade);
        }

        // How much of the sky's colour the light loses is kept between 0 and 1.
        [TestMethod]
        public void TheSkyNeutralityIsClamped()
        {
            RtNative.FramePacket packet = RtNative.NewFramePacket();

            RtPacketBuilder.Sky(ref packet, new Color(0.1f, 0.2f, 0.3f), 1.5f);

            CollectionAssert.AreEqual(new[] { 0.1f, 0.2f, 0.3f, 1f }, packet.SkyColor);

            RtPacketBuilder.Sky(ref packet, new Color(0.1f, 0.2f, 0.3f), -1f);

            Assert.AreEqual(0f, packet.SkyColor[3]);
        }

        // The body's sphere is relative to the camera; the sea's frame is cleared first and only written where there is one.
        [TestMethod]
        public void TheBodyIsRelativeToTheCameraAndTheSeaIsClearedFirst()
        {
            RtNative.FramePacket packet = RtNative.NewFramePacket();
            for (int i = 0; i < 12; i++) packet.Sea[i] = 9f;

            RtPacketBuilder.Body(ref packet, new Color(0.1f, 0.2f, 0.3f), 123f, new Vector4(110f, 220f, 330f, 600f),
                                 new RtPosition(10, 20, 30), false, Vector3.zero, Vector3.zero, Vector3.zero);

            CollectionAssert.AreEqual(new[] { 100f, 200f, 300f, 600f }, packet.BodySphere);
            CollectionAssert.AreEqual(new[] { 0.1f, 0.2f, 0.3f, 123f }, packet.Planet);
            CollectionAssert.AreEqual(new float[12], packet.Sea);

            RtPacketBuilder.Body(ref packet, new Color(0.1f, 0.2f, 0.3f), 123f, new Vector4(110f, 220f, 330f, 600f),
                                 new RtPosition(10, 20, 30), true, new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f), new Vector3(7f, 8f, 9f));

            CollectionAssert.AreEqual(new float[] { 1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0 }, packet.Sea);
        }

        // The sun's pass is always traced; the forward light comes with the diffuse light where the reflections pass does not take it.
        [TestMethod]
        public void TheSignalsFollowThePassesAndTheComposite()
        {
            Assert.AreEqual(RtNative.SignalSun, RtPacketBuilder.Signals(false, false, false));
            Assert.AreEqual(RtNative.SignalSun, RtPacketBuilder.Signals(false, false, true));
            Assert.AreEqual(RtNative.SignalSun | RtNative.SignalDiffuse, RtPacketBuilder.Signals(true, false, false));
            Assert.AreEqual(RtNative.SignalSun | RtNative.SignalSpecular, RtPacketBuilder.Signals(false, true, false));
            Assert.AreEqual(RtNative.SignalSun | RtNative.SignalDiffuse | RtNative.SignalForwardLight,
                            RtPacketBuilder.Signals(true, false, true));
            Assert.AreEqual(RtNative.SignalSun | RtNative.SignalSpecular | RtNative.SignalDiffuse | RtNative.SignalForwardLight,
                            RtPacketBuilder.Signals(false, true, true));
        }

        // A frame is always the same frame; each flag comes with its condition alone, and the parity check's flag passes through.
        [TestMethod]
        public void TheFlagsFollowTheirConditions()
        {
            Assert.AreEqual(RtNative.FlagSameFrame, RtPacketBuilder.Flags(false, false, false, false, false, false, 0u));
            Assert.AreEqual(RtNative.FlagSameFrame | RtNative.FlagDenoise, RtPacketBuilder.Flags(true, false, false, false, false, false, 0u));
            Assert.AreEqual(RtNative.FlagSameFrame | RtNative.FlagReset, RtPacketBuilder.Flags(false, true, false, false, false, false, 0u));
            Assert.AreEqual(RtNative.FlagSameFrame | RtNative.FlagMotionVectors, RtPacketBuilder.Flags(false, false, true, false, false, false, 0u));
            Assert.AreEqual(RtNative.FlagSameFrame | RtNative.FlagGuides, RtPacketBuilder.Flags(false, false, false, true, false, false, 0u));
            Assert.AreEqual(RtNative.FlagSameFrame | RtNative.FlagLegacySmoothness, RtPacketBuilder.Flags(false, false, false, false, true, false, 0u));
            Assert.AreEqual(RtNative.FlagSameFrame | RtNative.FlagSurfaceIntent, RtPacketBuilder.Flags(false, false, false, false, false, true, 0u));
            Assert.AreEqual(RtNative.FlagSameFrame | 1u << 20, RtPacketBuilder.Flags(false, false, false, false, false, false, 1u << 20));
        }

        // A matrix is written rows first, and a translation sits in the last column.
        [TestMethod]
        public void MatricesAreWrittenRowsFirst()
        {
            Matrix4x4 m = Matrix4x4.identity;
            m.m01 = 2f;
            m.m10 = 3f;
            var target = new float[16];

            RtPacketBuilder.Write(m, target);

            Assert.AreEqual(1f, target[0]);
            Assert.AreEqual(2f, target[1]);
            Assert.AreEqual(3f, target[4]);
            Assert.AreEqual(1f, target[15]);

            RtPacketBuilder.Write(RtPacketBuilder.Translation(1.0, -2.0, 3.0), target);

            Assert.AreEqual(1f, target[3]);
            Assert.AreEqual(-2f, target[7]);
            Assert.AreEqual(3f, target[11]);
            Assert.AreEqual(1f, target[10]);
        }
    }
}
