struct Vertex {
    @builtin(position) position: vec4<f32>,
    @location(0) uvq: vec3<f32>,
};
@group(0) @binding(0) var source: texture_2d<f32>;
@group(0) @binding(1) var source_sampler: sampler;

@vertex
fn vs_main(@location(0) position: vec2<f32>, @location(1) uvq: vec3<f32>) -> Vertex {
    var output: Vertex;
    output.position = vec4<f32>(position, 0.0, 1.0);
    output.uvq = uvq;
    return output;
}

@fragment
fn fs_main(vertex: Vertex) -> @location(0) vec4<f32> {
    let uv = vertex.uvq.xy / vertex.uvq.z;
    let color = textureSample(source, source_sampler, uv);
    return vec4<f32>(color.rgb * color.a, color.a);
}
