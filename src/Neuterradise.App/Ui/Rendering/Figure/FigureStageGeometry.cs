using System.Buffers.Binary;
using System.Numerics;

namespace Neuterradise.App.Ui.Figure;

internal sealed record FigureStageMesh(byte[] Vertices, byte[] Indices);

/// <summary>App-owned unit collectible stage; scaling and Theme accents never change durable model bytes.</summary>
internal static class FigureStageGeometry
{
    private const int Segments = 64;

    internal static FigureStageMesh Pedestal()
    {
        var mesh = new Builder();
        Ring(mesh, 0.95f, 0, 1, 0.008f);
        Ring(mesh, 1, 0.008f, 1, 0.052f);
        Ring(mesh, 1, 0.052f, 0.92f, 0.06f);
        for (var segment = 0; segment < Segments; segment++)
        {
            var a = Angle(segment); var b = Angle(segment + 1);
            mesh.Triangle(new(0, 0.06f, 0), new(a.X * 0.92f, 0.06f, a.Y * 0.92f),
                new(b.X * 0.92f, 0.06f, b.Y * 0.92f), Vector3.UnitY);
        }
        return mesh.Finish();
    }

    internal static FigureStageMesh AccentRim()
    {
        var mesh = new Builder();
        Ring(mesh, 1.001f, 0.043f, 1.001f, 0.049f);
        return mesh.Finish();
    }

    internal static FigureStageMesh ContactShadow()
    {
        var mesh = new Builder();
        mesh.Quad(new(-1, -0.001f, -1), new(-1, -0.001f, 1), new(1, -0.001f, 1), new(1, -0.001f, -1), Vector3.UnitY);
        return mesh.Finish();
    }

    private static Vector2 Angle(int segment)
    {
        var angle = segment * MathF.Tau / Segments;
        return new(MathF.Cos(angle), MathF.Sin(angle));
    }

    private static void Ring(Builder mesh, float lowerRadius, float lowerY, float upperRadius, float upperY)
    {
        for (var segment = 0; segment < Segments; segment++)
        {
            var a = Angle(segment); var b = Angle(segment + 1); var mid = Vector2.Normalize(a + b);
            var radial = upperY - lowerY;
            var vertical = lowerRadius - upperRadius;
            var normal = Vector3.Normalize(new(mid.X * radial, vertical, mid.Y * radial));
            mesh.Quad(new(a.X * lowerRadius, lowerY, a.Y * lowerRadius), new(a.X * upperRadius, upperY, a.Y * upperRadius),
                new(b.X * upperRadius, upperY, b.Y * upperRadius), new(b.X * lowerRadius, lowerY, b.Y * lowerRadius), normal);
        }
    }

    private sealed class Builder
    {
        private readonly List<float> _vertices = [];
        private readonly List<uint> _indices = [];

        public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
        {
            var index = (uint)(_vertices.Count / 12);
            Vertex(a, normal); Vertex(b, normal); Vertex(c, normal);
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0)
                _indices.AddRange([index, index + 2, index + 1]);
            else
                _indices.AddRange([index, index + 1, index + 2]);
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            var index = (uint)(_vertices.Count / 12);
            Vertex(a, normal); Vertex(b, normal); Vertex(c, normal); Vertex(d, normal);
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0)
                _indices.AddRange([index, index + 2, index + 1, index, index + 3, index + 2]);
            else
                _indices.AddRange([index, index + 1, index + 2, index, index + 2, index + 3]);
        }

        private void Vertex(Vector3 position, Vector3 normal)
        {
            var tangent = Vector3.Normalize(Math.Abs(normal.Y) > 0.95f ? Vector3.UnitX : Vector3.Cross(Vector3.UnitY, normal));
            _vertices.AddRange([position.X, position.Y, position.Z, normal.X, normal.Y, normal.Z,
                position.X * 0.5f + 0.5f, position.Z * 0.5f + 0.5f, tangent.X, tangent.Y, tangent.Z, 1]);
        }

        public FigureStageMesh Finish()
        {
            var vertices = new byte[_vertices.Count * 4];
            for (var i = 0; i < _vertices.Count; i++) BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(i * 4), _vertices[i]);
            var indices = new byte[_indices.Count * 4];
            for (var i = 0; i < _indices.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(indices.AsSpan(i * 4), _indices[i]);
            return new(vertices, indices);
        }
    }
}
