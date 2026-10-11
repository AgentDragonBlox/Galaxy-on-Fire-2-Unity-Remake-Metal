// DevIdentity.cs
// Remake: the developer's [DEV] tag in multiplayer. Pilot names aren't verified, so the tag needs a proof: the game holds
// the public half of an RSA key (2048 bits, e = 65537, the modulus below; it can only check signatures), the developer's
// PC alone the private half (persistentDataPath/dev.key, written once, never in the repository). Joining, a game with the
// key asks the server for a challenge (NetPlayer.DevHelloRpc), signs it (RSASSA-PKCS1-v1_5 over SHA-256 of
// "GoF2Remake-dev|" + the 32 random bytes) and sends the signature back; the server checks it here and sets the player's
// server-written dev flag (NetPlayer.IsDev). A fresh challenge each time, so a recorded answer is worthless, and only the
// server writes the flag, so a modified game can't give itself the tag (a modified server only fools its own players).
// The tag is cosmetic: no rights come with it. Plain BigInteger arithmetic (no platform crypto API: the same on IL2CPP,
// Android and the Editor's CoreCLR); SHA-256 from System.Security.Cryptography, which the profiles use too.

using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace GoF2Remake.Multiplayer
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class DevIdentity
    {
        /// <summary>The tag before the developer's name (chat, lock plate, squad and pilot lists, /players, the kill feed).</summary>
        public const string Tag = "<color=#f5c542>[DEV]</color>";
        public const string PlainTag = "[DEV]";
        public const int ChallengeBytes = 32, SignatureBytes = 256;

        const string ModulusHex = "ba4cc2373ac5112e6edca33a72b58611a5089ff429c17a94638357cf6a7e268fae9782c90ca41812b582bc09dc24c70c"
            + "f0f269786b754ef1b76e310165422aa3b77aea9ce3625dc6daf0ecd545d41d15f57d4595e1e65cc161546ff43bff7fb0"
            + "fb666c5d4a61ff5880773ee6a0aa98230629e2f4657b2ed882e85a6db1ff8cd5b2bd6fa47fb712fca6415fd3758b712f"
            + "7132faf49e116e90b5ccd266100206677f098b65ad7a04288cab1d80104c6a6363b7f3d08d9750e5933c844635d29874"
            + "d13f0306145fa3bb58b917c1a04a6b48233e62be9e3da6690b0430f6183f827332048a654defa036a061af54fc1130de"
            + "0cc719e5b11570c5f6dde65826e83e67";
        static readonly BigInteger N = Hex(ModulusHex), E = 65537;
        static readonly byte[] Context = Encoding.UTF8.GetBytes("GoF2Remake-dev|");
        // DER DigestInfo for SHA-256 (RFC 8017 9.2, note 1).
        static readonly byte[] Sha256Info = { 0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20 };

        static bool loaded;
        static BigInteger privateExponent;

        /// <summary>The private key's file (on the developer's PC only).</summary>
        public static string KeyPath => Path.Combine(Application.persistentDataPath, "dev.key");

        /// <summary>This game holds the developer's private key (its modulus matches the public one).</summary>
        public static bool HasKey
        {
            get
            {
                if (!loaded)
                {
                    loaded = true;
                    privateExponent = BigInteger.Zero;
                    try
                    {
                        if (!File.Exists(KeyPath)) return false;
                        BigInteger n = BigInteger.Zero, d = BigInteger.Zero;
                        foreach (var line in File.ReadAllLines(KeyPath))
                        {
                            if (line.StartsWith("n=")) n = Hex(line.Substring(2).Trim());
                            else if (line.StartsWith("d=")) d = Hex(line.Substring(2).Trim());
                        }
                        if (n == N && d > BigInteger.Zero) privateExponent = d;
                        else Debug.LogWarning("DevIdentity: dev.key doesn't match this game's public key");
                    }
                    catch (Exception e) { Debug.LogWarning($"DevIdentity: dev.key unreadable ({e.Message})"); }
                }
                return privateExponent > BigInteger.Zero;
            }
        }

        /// <summary>A fresh random challenge (the server's).</summary>
        public static byte[] NewChallenge()
        {
            var c = new byte[ChallengeBytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(c);
            return c;
        }

        /// <summary>The signature of 'challenge' with the private key; null without it.</summary>
        public static byte[] Sign(byte[] challenge)
        {
            if (challenge == null || challenge.Length != ChallengeBytes || !HasKey) return null;
            var s = BigInteger.ModPow(Encoded(challenge), privateExponent, N);
            return ToBytes(s);
        }

        /// <summary>The server: 'signature' is the developer key's signature of 'challenge'.</summary>
        public static bool Verify(byte[] challenge, byte[] signature)
        {
            if (challenge == null || challenge.Length != ChallengeBytes || signature == null || signature.Length != SignatureBytes) return false;
            var s = new BigInteger(signature, isUnsigned: true, isBigEndian: true);
            if (s >= N) return false;
            return BigInteger.ModPow(s, E, N) == Encoded(challenge);
        }

        /// <summary>EMSA-PKCS1-v1_5 (RFC 8017 9.2): 00 01 FF..FF 00 DigestInfo SHA-256(context + challenge), as a number.</summary>
        static BigInteger Encoded(byte[] challenge)
        {
            byte[] hash;
            using (var sha = SHA256.Create())
            {
                var msg = new byte[Context.Length + challenge.Length];
                Buffer.BlockCopy(Context, 0, msg, 0, Context.Length);
                Buffer.BlockCopy(challenge, 0, msg, Context.Length, challenge.Length);
                hash = sha.ComputeHash(msg);
            }
            var em = new byte[SignatureBytes];
            em[0] = 0x00;
            em[1] = 0x01;
            int t = Sha256Info.Length + hash.Length;
            for (int i = 2; i < SignatureBytes - t - 1; i++) em[i] = 0xff;
            em[SignatureBytes - t - 1] = 0x00;
            Buffer.BlockCopy(Sha256Info, 0, em, SignatureBytes - t, Sha256Info.Length);
            Buffer.BlockCopy(hash, 0, em, SignatureBytes - hash.Length, hash.Length);
            return new BigInteger(em, isUnsigned: true, isBigEndian: true);
        }

        static byte[] ToBytes(BigInteger v)
        {
            var raw = v.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (raw.Length == SignatureBytes) return raw;
            var padded = new byte[SignatureBytes];
            Buffer.BlockCopy(raw, 0, padded, SignatureBytes - raw.Length, raw.Length);
            return padded;
        }

        static BigInteger Hex(string hex) => BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
