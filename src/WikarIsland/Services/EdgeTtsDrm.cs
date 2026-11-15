using System;
using System.Text;

namespace WikarIsland.Services;

/// <summary>
/// Токен Sec-MS-GEC версии 2 для голосов Microsoft Edge («Читать вслух»).
/// С октября 2026 сервис принимает только его. Порт generate_sec_ms_gec_v2 из rany2/edge-tts (MIT):
/// SHA-256 с особым начальным состоянием и одной подменённой константой плюс хеш CS64.
/// Вся арифметика 32-битная без знака — переполнение в C# здесь и есть нужное «по модулю 2^32».
/// </summary>
public static class EdgeTtsDrm
{
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    public const string ChromiumVersion = "143.0.3650.75";
    public const string SecMsGecVersion = "2-" + ChromiumVersion;

    private const long WinEpoch = 11644473600;

    /// <summary>Поправка на часы компьютера: если сервер ответил 403, подстраиваемся под его время.</summary>
    public static double ClockSkewSeconds { get; set; }

    private static readonly uint[] Initial =
        { 0x6A09E667u, 0xBB67AE85u, 0x3C6EF372u, 0xA54FF53Au, 0x510E527Fu, 0x9B05688Cu, 0x1F83D9ABu, 0x5BE0CD19u };

    private static readonly uint[] State =
        { 0xFD9FBEA2u, 0x91243CE7u, 0xBE499094u, 0xCD2E1E0Au, 0x6C006F49u, 0x04BD35ABu, 0xFEB0A7E4u, 0x03EFFBD2u };

    private static readonly uint[] Schedule =
    {
        0xEB24BF9Eu, 0x1558691Fu, 0xA0C09EE2u, 0x0CFA32BAu, 0xA622EE8Cu, 0xD1DD9491u, 0xC0DD25A0u,
        0xF9E70AB6u, 0xF0C31573u, 0x7058CE4Eu, 0x7188E606u, 0x00E5AB33u, 0xD258B060u, 0x72766564u,
    };

    private static readonly uint[] K =
    {
        0x428A2F98u, 0x71374491u, 0xB5C0FBCFu, 0xE9B5DBA5u, 0x3956C25Bu, 0x59F111F1u, 0x923F82A4u, 0xAB1C5ED5u,
        0xD807AA98u, 0x12835B01u, 0x243185BEu, 0x550C7DC3u, 0x72BE5D74u, 0x80DEB1FEu, 0x9BDC06A7u, 0xC19BF174u,
        0xE49B69C1u, 0xEFBE4786u, 0x0FC19DC6u, 0x240CA1CCu, 0x2DE92C6Fu, 0x4A7484AAu, 0x5CB0A9DCu, 0x76F988DAu,
        0x983E5152u, 0xA831C66Du, 0xB00327C8u, 0xBF597FC7u, 0xC6E00BF3u, 0xD5A79147u, 0x06CA6351u, 0x14292967u,
        0x27B70A85u, 0x2E1B2138u, 0x4D2C6DFCu, 0x53380D13u, 0x650A7354u, 0x766A0ABBu, 0x81C2C92Eu, 0x92722C85u,
        0xA2BFE8A1u, 0xA81A664Bu, 0xC24B8B70u, 0xC76C51A3u, 0xD192E819u, 0xD6990624u, 0xF40E3585u, 0x106AA070u,
        0x19A4C116u, 0x1E376C08u, 0x2748774Cu, 0x34B0BCB5u, 0x391C0CB3u, 0x4ED8AA4Au, 0x5B9CCA4Fu, 0x682E6FF3u,
        0x748F82EEu, 0x78A5636Fu, 0x84C87814u, 0x8CC70208u, 0x90BEFFFAu, 0xA4506CEBu, 0xBEF9A3F7u, 0xC67178F2u,
    };

    private const uint Adjustment = 0xC9DC06A7u;
    private const uint Sigma0Adjustment = 0x059C0B80u;
    private const uint Sigma1Adjustment = 0x000B92C0u;

    private static readonly uint[,] Cs64Coefficients =
    {
        { 0xE82E91B1u, 0xB32700D0u }, { 0x44DE24C6u, 0x88DFF51Du }, { 0x3BEB65BCu, 0x29514038u }, { 0xC89C5933u, 0xB328CBDFu },
        { 0x8D1EC211u, 0xD405B4D5u }, { 0x314EF5D5u, 0x26E55F3Cu }, { 0xE92491B1u, 0x607801C6u }, { 0xF0A5B9D5u, 0x82B2C1FBu },
        { 0x06064CF3u, 0xFA51A3DAu }, { 0xDA79FA60u, 0x207CC464u }, { 0xBED20560u, 0x3C3C74F3u }, { 0x89F9C9F8u, 0xD2D14B61u },
        { 0x35FA38C6u, 0xBB04A2C1u }, { 0xCB49E3C6u, 0xC5626167u },
    };

    private const uint Cs64Factor = 0x82893933u;
    private const uint Cs64Adjustment0 = 0xF2F90B1Au;
    private const uint Cs64Adjustment1 = 0x0B1A0000u;

    /// <summary>Токен для текущего времени (с поправкой на часы).</summary>
    public static string Generate() =>
        Generate(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + ClockSkewSeconds);

    public static string Generate(double unixSeconds)
    {
        long secs = (long)Math.Floor(unixSeconds) + WinEpoch;
        secs -= secs % 300;
        byte[] data = Encoding.ASCII.GetBytes($"{secs * 10_000_000L}{TrustedClientToken}");
        byte[] digest = Sha256(data);
        (uint c0, uint c1) = Cs64(data, digest);
        var cs = new byte[8];
        WriteLe(cs, 0, c0);
        WriteLe(cs, 4, c1);
        return Convert.ToHexString(cs) + "-" + Convert.ToHexString(digest);
    }

    /// <summary>Самопроверка на эталонах из edge-tts: запускается в CI (WikarIsland.exe --selftest).</summary>
    public static bool SelfTest(out string report)
    {
        (double t, string expected)[] vectors =
        {
            (1700000000, "2CD23A2CA0B2595A-F3C846C370E05F9CC83ACC5F995C5F5D87A4D0F14CE8936A0F902E64099E38A2"),
            (1794733793.5, "A73485E8753E45E8-6633726701EEDC56BA66F437C9883DAAAA4C8FCAC1778F92145450ADD2DA41F4"),
            (1794730000, "C6129BCFC638A370-E491E5554C3E7E5D742529B8D075B687A05D429ED8E428059385ADF48ADF82C0"),
            (1800000123, "99E54519B490BE5C-E162DB0977FBDCB9B86DD8148BF1BCDC3C0303176CE39852A42B415935C20654"),
        };
        var sb = new StringBuilder();
        bool ok = true;
        foreach (var (t, expected) in vectors)
        {
            string got = Generate(t);
            bool same = got == expected;
            ok &= same;
            sb.AppendLine($"{(same ? "OK  " : "FAIL")} {t} {got}");
        }
        report = sb.ToString();
        return ok;
    }

    // ---------- SHA-256 с изменённым началом ----------

    private static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));
    private static uint Rol16(uint x) => (x << 16) | (x >> 16);
    private static uint Sig0(uint x) => Rotr(x, 7) ^ Rotr(x, 18) ^ (x >> 3);
    private static uint Sig1(uint x) => Rotr(x, 17) ^ Rotr(x, 19) ^ (x >> 10);

    private static void Expand(uint[] w, int first)
    {
        for (int i = first; i < 64; i++) w[i] = Sig1(w[i - 2]) + w[i - 7] + Sig0(w[i - 15]) + w[i - 16];
    }

    private static uint[] Rounds(uint[] state, uint[] w, int first, uint[] k)
    {
        uint a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7];
        for (int i = first; i < 64; i++)
        {
            uint s1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
            uint ch = g ^ (e & (f ^ g));
            uint t1 = h + s1 + ch + k[i] + w[i];
            uint s0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
            uint maj = (a & b) | (c & (a | b));
            uint t2 = s0 + maj;
            h = g;
            g = f;
            f = e;
            e = d + t1;
            d = c;
            c = b;
            b = a;
            a = t1 + t2;
        }
        return new[] { a, b, c, d, e, f, g, h };
    }

    private static uint[] Init(byte[] data)
    {
        var w = new uint[64];
        w[14] = ((uint)data[0] << 16) | ((uint)data[1] << 8) | data[2];
        w[15] = ((uint)data[3] << 24) | ((uint)data[4] << 16) | ((uint)data[5] << 8) | data[6];
        for (int i = 16; i < 30; i++)
        {
            uint v = Schedule[i - 16];
            v += i == 16 ? Sig1(w[i - 2]) ^ Sigma1Adjustment : Sig1(w[i - 2]);
            if (i >= 21) v += w[i - 7];
            if (i == 29) v += Sig0(w[i - 15]) ^ Sigma0Adjustment;
            w[i] = v;
        }
        w[30] = Sig1(w[28]) + w[23] + Sig0(w[15]) + w[14] + Adjustment - K[14];
        Expand(w, 31);
        var kb = (uint[])K.Clone();
        kb[14] = Adjustment;
        uint[] working = Rounds(State, w, 14, kb);
        var result = new uint[8];
        for (int i = 0; i < 8; i++) result[i] = Initial[i] + working[i];
        return result;
    }

    private static byte[] Sha256(byte[] data)
    {
        if (data.Length < 7) throw new ArgumentException("Слишком короткие данные");
        int length = 57 + data.Length;
        int pad = ((55 - length) % 64 + 64) % 64;
        int rest = data.Length - 7;
        int total = rest + 1 + pad + 8;
        var p = new byte[total];
        Array.Copy(data, 7, p, 0, rest);
        p[rest] = 0x80;
        ulong bits = (ulong)length * 8;
        for (int i = 0; i < 8; i++) p[total - 1 - i] = (byte)(bits >> (8 * i));

        uint[] state = Init(data);
        for (int off = 0; off < total; off += 64)
        {
            var w = new uint[64];
            for (int i = 0; i < 16; i++)
                w[i] = ((uint)p[off + 4 * i] << 24) | ((uint)p[off + 4 * i + 1] << 16) | ((uint)p[off + 4 * i + 2] << 8) | p[off + 4 * i + 3];
            Expand(w, 16);
            uint[] working = Rounds(state, w, 0, K);
            for (int i = 0; i < 8; i++) state[i] += working[i];
        }

        var digest = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            digest[4 * i] = (byte)(state[i] >> 24);
            digest[4 * i + 1] = (byte)(state[i] >> 16);
            digest[4 * i + 2] = (byte)(state[i] >> 8);
            digest[4 * i + 3] = (byte)state[i];
        }
        return digest;
    }

    // ---------- CS64 ----------

    private static (uint, uint) Cs64(byte[] data, byte[] digest)
    {
        uint keyA = ReadLe(digest, 0) | 1;
        uint keyB = ReadLe(digest, 4) | 1;
        uint[] factors = { keyA * Cs64Factor, keyB * Cs64Factor };

        int n = Cs64Coefficients.GetLength(0);
        var terms = new System.Collections.Generic.List<uint>();
        var offsets = new System.Collections.Generic.List<uint>();
        for (int i = 0; i < n; i++)
        {
            terms.Add(Cs64Coefficients[i, 0] * factors[i % 2]);
            offsets.Add(Cs64Coefficients[i, 1]);
        }

        // слова little-endian из (0x00 + данные), дополненные нулями до чётного числа слов
        var src = new byte[data.Length + 1];
        Array.Copy(data, 0, src, 1, data.Length);
        int count = (src.Length + 3) / 4;
        var words = new System.Collections.Generic.List<uint>();
        for (int i = 0; i < count; i++)
        {
            uint word = 0;
            for (int j = 0; j < 4; j++)
                if (4 * i + j < src.Length) word |= (uint)src[4 * i + j] << (8 * j);
            words.Add(word);
        }
        if (words.Count % 2 == 1) words.Add(0);

        for (int i = 0; i < words.Count; i++)
        {
            uint word = words[i];
            uint term = word * (i % 2 == 0 ? keyA : keyB);
            uint offset = Rol16(word) * (i % 2 == 0 ? 0xEF0569FBu : 0xC31713DBu);
            if (i == 0)
            {
                term += Cs64Adjustment0 * factors[0];
                offset += Cs64Adjustment1;
            }
            terms.Add(term);
            offsets.Add(offset);
        }

        uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
        for (int i = 0; i < terms.Count; i += 2)
        {
            uint x = terms[i], y = terms[i + 1];
            uint t = Rol16(offsets[i] + x);
            uint q = t * 0x76F50F34u;
            swapAcc += Rol16(q) * 0xC3EFEA97u + q;
            t = Rol16(offsets[i + 1] + y);
            q = t * 0x1881CE3Cu;
            swapLast = Rol16(q) * 0x35BD1EC9u + q;
            swapAcc += swapLast;

            state = state * keyA + x;
            state = Rol16(state) * 0xCF98B111u;
            state = Rol16(state) * 0x87085B9Fu;
            state = Rol16(state) * 0x12CEB96Du;
            state = Rol16(state) * 0x257E1D83u;
            acc += state;
            t = state * keyB + y;
            t = Rol16(t) * 0xA27416F5u;
            t = Rol16(t) * 0xD38396FFu;
            state = Rol16(Rol16(t) * 0x7C932B89u) * 0xBFA49F69u;
            acc += state;
        }
        return (swapLast ^ state, swapAcc ^ acc);
    }

    private static uint ReadLe(byte[] b, int i) =>
        b[i] | ((uint)b[i + 1] << 8) | ((uint)b[i + 2] << 16) | ((uint)b[i + 3] << 24);

    private static void WriteLe(byte[] b, int i, uint v)
    {
        b[i] = (byte)v;
        b[i + 1] = (byte)(v >> 8);
        b[i + 2] = (byte)(v >> 16);
        b[i + 3] = (byte)(v >> 24);
    }
}
