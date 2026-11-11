namespace SimpleVpn.Core.Tests;

/// <summary>Sample share links with fake credentials (documentation IP ranges, example.com).</summary>
internal static class TestLinks
{
    public const string Uuid = "0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b";

    /// <summary>The main production setup: VLESS + XHTTP + TLS, as exported by 3x-ui.</summary>
    public const string XhttpTls =
        "vless://" + Uuid + "@vpn.example.com:443?type=xhttp&encryption=none&security=tls&path=%2Fxh&host=vpn.example.com" +
        "&mode=auto&sni=vpn.example.com&fp=chrome&alpn=h2%2Chttp%2F1.1" +
        "&extra=%7B%22xPaddingBytes%22%3A%22100-1000%22%7D" +
        "#%D0%9D%D0%B8%D0%B4%D0%B5%D1%80%D0%BB%D0%B0%D0%BD%D0%B4%D1%8B";

    public const string RealityVision =
        "vless://" + Uuid + "@203.0.113.10:443?type=tcp&security=reality&encryption=none&flow=xtls-rprx-vision" +
        "&pbk=Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw&sid=6ba85179e30d4fc2&sni=www.microsoft.com&fp=chrome&spx=%2F#reality";

    public const string WsTlsIpv6 =
        "vless://" + Uuid + "@[2001:db8::1]:8443?type=ws&security=tls&path=%2Fws%3Fed%3D2048&host=cdn.example.com&sni=cdn.example.com#ws";

    public const string GrpcTls =
        "vless://" + Uuid + "@g.example.com:443?type=grpc&security=tls&serviceName=grpc-svc&sni=g.example.com#grpc";

    /// <summary>Self-signed server: legacy allowInsecure plus a pinned certificate hash.</summary>
    public const string PinnedCert =
        "vless://" + Uuid + "@203.0.113.20:443?type=xhttp&security=tls&sni=self.example.com&allowInsecure=1" +
        "&pcs=e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855&vcn=self.example.com#pinned";

    public static TheoryData<string> All => [XhttpTls, RealityVision, WsTlsIpv6, GrpcTls, PinnedCert];
}
