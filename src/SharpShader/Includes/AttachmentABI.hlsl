#ifndef SHARPSHADER_ATTACHMENT_ABI_INCLUDED
#define SHARPSHADER_ATTACHMENT_ABI_INCLUDED

// Stable author-facing syntax only. The authoritative attachment mapping is the
// explicit ShaderAttachmentInterface compile input and is validated per artifact.
#define SHARPSHADER_ATTACHMENT_ABI_REVISION 1
#define SHARPSHADER_ATTACHMENT_PRIVATE_TABLE 65535
#define SHARPSHADER_ATTACHMENT_PROVISIONAL_SPIRV_SET 65535

#define SHARPSHADER_DETAIL_JOIN_INNER(left, right) left##right
#define SHARPSHADER_DETAIL_JOIN(left, right) \
    SHARPSHADER_DETAIL_JOIN_INNER(left, right)
#define SHARPSHADER_DETAIL_REGISTER(resource_prefix, slot, table) \
    register( \
        SHARPSHADER_DETAIL_JOIN(resource_prefix, slot), \
        SHARPSHADER_DETAIL_JOIN(space, table))

#define SHARPSHADER_COLOR_OUTPUT(location) \
    SHARPSHADER_DETAIL_JOIN(SV_Target, location)
#define SHARPSHADER_DEPTH_OUTPUT SV_Depth
#define SHARPSHADER_DEPTH_GREATER_EQUAL_OUTPUT SV_DepthGreaterEqual
#define SHARPSHADER_DEPTH_LESS_EQUAL_OUTPUT SV_DepthLessEqual
#define SHARPSHADER_STENCIL_REFERENCE_OUTPUT SV_StencilRef

#define SHARPSHADER_DECLARE_COLOR_OUTPUT(value_type, name, location) \
    SHARPSHADER_OUTPUT_LOCATION(location) \
    value_type name : SHARPSHADER_COLOR_OUTPUT(location)
#define SHARPSHADER_DECLARE_DUAL_SOURCE_COLOR_OUTPUT(value_type, name) \
    SHARPSHADER_OUTPUT_LOCATION(0) \
    SHARPSHADER_OUTPUT_INDEX(1) \
    value_type name : SV_Target1
#define SHARPSHADER_DECLARE_DEPTH_OUTPUT(value_type, name) \
    value_type name : SHARPSHADER_DEPTH_OUTPUT
#define SHARPSHADER_DECLARE_DEPTH_GREATER_EQUAL_OUTPUT(value_type, name) \
    value_type name : SHARPSHADER_DEPTH_GREATER_EQUAL_OUTPUT
#define SHARPSHADER_DECLARE_DEPTH_LESS_EQUAL_OUTPUT(value_type, name) \
    value_type name : SHARPSHADER_DEPTH_LESS_EQUAL_OUTPUT
#define SHARPSHADER_DECLARE_STENCIL_REFERENCE_OUTPUT(name) \
    uint name : SHARPSHADER_STENCIL_REFERENCE_OUTPUT

#if defined(__spirv__) || defined(SHARPSHADER_TARGET_SPIRV)
#define SHARPSHADER_ATTACHMENT_TARGET_SPIRV 1
#define SHARPSHADER_ATTACHMENT_TARGET_DXIL 0
#define SHARPSHADER_INPUT_ATTACHMENT(_attachment_index) [[vk::input_attachment_index(_attachment_index)]]
#define SHARPSHADER_OUTPUT_LOCATION(_output_location) [[vk::location(_output_location)]]
#define SHARPSHADER_OUTPUT_INDEX(_output_index) [[vk::index(_output_index)]]
#define SHARPSHADER_DECLARE_LOCAL_INPUT_2D(value_type, name, index) \
    SHARPSHADER_INPUT_ATTACHMENT(index) \
    [[vk::binding(index, SHARPSHADER_ATTACHMENT_PROVISIONAL_SPIRV_SET)]] \
    SubpassInput<value_type> name
#define SHARPSHADER_DECLARE_LOCAL_INPUT_2D_MS(value_type, name, index) \
    SHARPSHADER_INPUT_ATTACHMENT(index) \
    [[vk::binding(index, SHARPSHADER_ATTACHMENT_PROVISIONAL_SPIRV_SET)]] \
    SubpassInputMS<value_type> name
// Vulkan subpass inputs select the current framebuffer layer implicitly.
#define SHARPSHADER_DECLARE_LOCAL_INPUT_2D_ARRAY(value_type, name, index) \
    SHARPSHADER_DECLARE_LOCAL_INPUT_2D(value_type, name, index)
#define SHARPSHADER_DECLARE_LOCAL_INPUT_2D_MS_ARRAY(value_type, name, index) \
    SHARPSHADER_DECLARE_LOCAL_INPUT_2D_MS(value_type, name, index)
#define SHARPSHADER_LOAD_LOCAL_INPUT(name, pixel_position) name.SubpassLoad()
#define SHARPSHADER_LOAD_LOCAL_INPUT_MS(name, pixel_position, sample_index) \
    name.SubpassLoad(sample_index)
#define SHARPSHADER_LOAD_LOCAL_INPUT_ARRAY(name, pixel_position, layer) \
    name.SubpassLoad()
#define SHARPSHADER_LOAD_LOCAL_INPUT_MS_ARRAY( \
    name, pixel_position, layer, sample_index) name.SubpassLoad(sample_index)
#define SHARPSHADER_HAS_RASTER_ORDERED_TEXTURE_PATH 1
#define SHARPSHADER_DECLARE_RASTER_ORDERED_ATTACHMENT_2D( \
    value_type, name, logical_attachment_id, input_index) \
    [[vk::binding(logical_attachment_id, SHARPSHADER_ATTACHMENT_PROVISIONAL_SPIRV_SET)]] \
    RasterizerOrderedTexture2D<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER( \
            u, logical_attachment_id, SHARPSHADER_ATTACHMENT_PRIVATE_TABLE)
#define SHARPSHADER_DECLARE_RASTER_ORDERED_ATTACHMENT_2D_ARRAY( \
    value_type, name, logical_attachment_id, input_index) \
    [[vk::binding(logical_attachment_id, SHARPSHADER_ATTACHMENT_PROVISIONAL_SPIRV_SET)]] \
    RasterizerOrderedTexture2DArray<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER( \
            u, logical_attachment_id, SHARPSHADER_ATTACHMENT_PRIVATE_TABLE)
#else
#define SHARPSHADER_ATTACHMENT_TARGET_SPIRV 0
#define SHARPSHADER_ATTACHMENT_TARGET_DXIL 1
#define SHARPSHADER_INPUT_ATTACHMENT(index)
#define SHARPSHADER_OUTPUT_LOCATION(location)
#define SHARPSHADER_OUTPUT_INDEX(index)
#define SHARPSHADER_DECLARE_LOCAL_INPUT_2D(value_type, name, index) \
    Texture2D<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER( \
            t, index, SHARPSHADER_ATTACHMENT_PRIVATE_TABLE)
#define SHARPSHADER_DECLARE_LOCAL_INPUT_2D_MS(value_type, name, index) \
    Texture2DMS<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER( \
            t, index, SHARPSHADER_ATTACHMENT_PRIVATE_TABLE)
#define SHARPSHADER_DECLARE_LOCAL_INPUT_2D_ARRAY(value_type, name, index) \
    Texture2DArray<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER( \
            t, index, SHARPSHADER_ATTACHMENT_PRIVATE_TABLE)
#define SHARPSHADER_DECLARE_LOCAL_INPUT_2D_MS_ARRAY(value_type, name, index) \
    Texture2DMSArray<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER( \
            t, index, SHARPSHADER_ATTACHMENT_PRIVATE_TABLE)
#define SHARPSHADER_LOAD_LOCAL_INPUT(name, pixel_position) \
    name.Load(int3(pixel_position, 0))
#define SHARPSHADER_LOAD_LOCAL_INPUT_MS(name, pixel_position, sample_index) \
    name.Load(pixel_position, sample_index)
#define SHARPSHADER_LOAD_LOCAL_INPUT_ARRAY(name, pixel_position, layer) \
    name.Load(int4(pixel_position, layer, 0))
#define SHARPSHADER_LOAD_LOCAL_INPUT_MS_ARRAY( \
    name, pixel_position, layer, sample_index) \
    name.Load(int3(pixel_position, layer), sample_index)
#define SHARPSHADER_HAS_RASTER_ORDERED_TEXTURE_PATH 1
#define SHARPSHADER_DECLARE_RASTER_ORDERED_ATTACHMENT_2D( \
    value_type, name, logical_attachment_id, input_index) \
    RasterizerOrderedTexture2D<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER( \
            u, logical_attachment_id, SHARPSHADER_ATTACHMENT_PRIVATE_TABLE)
#define SHARPSHADER_DECLARE_RASTER_ORDERED_ATTACHMENT_2D_ARRAY( \
    value_type, name, logical_attachment_id, input_index) \
    RasterizerOrderedTexture2DArray<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER( \
            u, logical_attachment_id, SHARPSHADER_ATTACHMENT_PRIVATE_TABLE)
#endif

#define SHARPSHADER_LOAD_RASTER_ORDERED_ATTACHMENT( \
    texture_name, pixel_position) texture_name[pixel_position]
#define SHARPSHADER_LOAD_RASTER_ORDERED_ATTACHMENT_ARRAY( \
    texture_name, pixel_position, layer) \
    texture_name[int3(pixel_position, layer)]
#define SHARPSHADER_STORE_RASTER_ORDERED_ATTACHMENT( \
    texture_name, output_lvalue, pixel_position, value) \
    do { \
        (output_lvalue) = (value); \
        texture_name[pixel_position] = (output_lvalue); \
    } while (false)
#define SHARPSHADER_STORE_RASTER_ORDERED_ATTACHMENT_ARRAY( \
    texture_name, output_lvalue, pixel_position, layer, value) \
    do { \
        (output_lvalue) = (value); \
        texture_name[int3(pixel_position, layer)] = (output_lvalue); \
    } while (false)

#define SHARPSHADER_DECLARE_SAMPLED_FEEDBACK_TEXTURE_2D( \
    value_type, name, table, slot) \
    Texture2D<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER(t, slot, table)
#define SHARPSHADER_DECLARE_SAMPLED_FEEDBACK_TEXTURE_2D_MS( \
    value_type, name, table, slot) \
    Texture2DMS<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER(t, slot, table)
#define SHARPSHADER_DECLARE_SAMPLED_FEEDBACK_TEXTURE_2D_ARRAY( \
    value_type, name, table, slot) \
    Texture2DArray<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER(t, slot, table)
#define SHARPSHADER_DECLARE_SAMPLED_FEEDBACK_TEXTURE_2D_MS_ARRAY( \
    value_type, name, table, slot) \
    Texture2DMSArray<value_type> name : \
        SHARPSHADER_DETAIL_REGISTER(t, slot, table)
#define SHARPSHADER_SAMPLE_FEEDBACK(texture_name, sampler_name, uv) \
    texture_name.Sample(sampler_name, uv)
#define SHARPSHADER_LOAD_SAMPLED_FEEDBACK(texture_name, pixel_position) \
    texture_name.Load(int3(pixel_position, 0))
#define SHARPSHADER_LOAD_SAMPLED_FEEDBACK_MS(texture_name, pixel_position, sample_index) \
    texture_name.Load(pixel_position, sample_index)
#define SHARPSHADER_SAMPLE_FEEDBACK_ARRAY( \
    texture_name, sampler_name, uv, layer) \
    texture_name.Sample(sampler_name, float3(uv, layer))
#define SHARPSHADER_LOAD_SAMPLED_FEEDBACK_ARRAY( \
    texture_name, pixel_position, layer) \
    texture_name.Load(int4(pixel_position, layer, 0))
#define SHARPSHADER_LOAD_SAMPLED_FEEDBACK_MS_ARRAY( \
    texture_name, pixel_position, layer, sample_index) \
    texture_name.Load(int3(pixel_position, layer), sample_index)

#endif