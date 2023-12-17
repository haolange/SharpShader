# InfinityShaderCompiler
A Shaderlab like shader framework.

# Example Shader
Here is an example to use:

```HLSL
Shader "Test/UnlitShader"
{
	Properties
	{
        //[Header(Albedo)]
        _AlbedoMap ("AlbedoMap", 2D) = "white" {}
        _AlbedoColor("AlbedoColor", Color) = (1, 0.25, 0.2, 1)

		//[Header(Microface)]
		_IntValue("IntValue", Int) = 233
		_SpecularValue("SpecularValue", Range(0, 1)) = 0.5
		_MetallicValue("MetallicValue", Range(0, 1)) = 1
        _RoughnessValue("RoughnessValue", Range(0, 1)) = 0.66
	}

	Category
	{
		Tags {"Queue" = "Geometry" "RenderType" = "Opaque" "RenderPipeline" = "InfinityRenderPipeline"}

		Pass
		{
			Tags {"Name" = "Depth" "Type" = "Graphics"}
			ZWrite On ZTest LEqual Cull Back 
			ColorMask 0 

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag

			#include "../Private/Common.hlsl"

			struct Attributes
			{
				float2 uv0 : TEXCOORD0;
				float4 vertexOS : POSITION;
			};

			struct Varyings
			{
				float2 uv0 : TEXCOORD0;
			};

			cbuffer PerCamera : register(b0, space0);
			{
				float4x4 matrix_VP;
			};
			cbuffer PerObject : register(b1, space0);
			{
				float4x4 matrix_World;
				float4x4 matrix_Object;
			};
			cbuffer PerMaterial : register(b2, space0);
			{
				int _IntValue;
				float _SpecularValue;
				float _MetallicValue;
				float _RoughnessValue;
				float4 _AlbedoColor;
			};	
			Texture2D _AlbedoMap : register(t0, space0); 
			SamplerState sampler_AlbedoMap : register(s0, space0);

			Varyings Vert(Attributes In, out float4 vertexCS : SV_POSITION)
			{
				Varyings Out = (Varyings)0;

				Out.uv0 = In.uv0;
				float4 WorldPos = mul(matrix_World, float4(In.vertexOS.xyz, 1.0));
				vertexCS = mul(matrix_VP, WorldPos);
				return Out;
			}

			float4 Frag(Varyings In) : SV_Target
			{
				return 0;
			}
			ENDHLSL
		}

		Pass
		{
			Tags {"Name" = "GBuffer" "Type" = "Graphics"}
			ZWrite On ZTest LEqual Cull Back 

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag

			#include "../Private/Common.hlsl"

			struct Attributes
			{
				float2 uv0 : TEXCOORD0;
				float3 normalOS : NORMAL;
				float4 vertexOS : POSITION;
			};

			struct Varyings
			{
				float2 uv0 : TEXCOORD0;
				float3 normalWS : TEXCOORD1;
				float4 vertexWS : TEXCOORD2;
			};

			cbuffer PerCamera : register(b0, space0);
			{
				float4x4 matrix_VP;
			};
			cbuffer PerObject : register(b1, space0);
			{
				float4x4 matrix_World;
				float4x4 matrix_Object;
			};
			cbuffer PerMaterial : register(b2, space0);
			{
				int _IntValue;
				float _SpecularValue;
				float _MetallicValue;
				float _RoughnessValue;
				float4 _AlbedoColor;
			};	
			Texture2D _AlbedoMap : register(t0, space0); 
			SamplerState sampler_AlbedoMap : register(s0, space0);

			Varyings Vert(Attributes In, out float4 vertexCS : SV_POSITION)
			{
				Varyings Out = (Varyings)0;

				Out.uv0 = In.uv0;
				Out.normalWS = normalize(mul((float3x3)matrix_World, In.normalOS));
				Out.vertexWS = mul(matrix_World, float4(In.vertexOS.xyz, 1.0));
				vertexCS = mul(matrix_VP, Out.vertexWS);
				return Out;
			}
			
			void Frag(Varyings In, out float4 GBufferA : SV_Target0, out float4 GBufferB : SV_Target1)
			{
				float3 albedo = _AlbedoMap.Sample(sampler_AlbedoMap, In.uv0).rgb;

				GBufferA = float4(albedo, 1);
				GBufferB = float4((In.normalWS * 0.5 + 0.5), 1);
			}
			ENDHLSL
		}

		Pass
		{
			Tags {"Name" = "Forward" "Type" = "Graphics"}
			ZWrite Off ZTest Equal Cull Back 

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag

			#include "../Private/Common.hlsl"

			struct Attributes
			{
				float2 uv0 : TEXCOORD0;
				float3 normalOS : NORMAL;
				float4 vertexOS : POSITION;
			};

			struct Varyings
			{
				float2 uv0 : TEXCOORD0;
				float3 normalWS : TEXCOORD1;
				float4 vertexWS : TEXCOORD2;
			};

			cbuffer PerCamera : register(b0, space0);
			{
				float4x4 matrix_VP;
			};
			cbuffer PerObject : register(b1, space0);
			{
				float4x4 matrix_World;
				float4x4 matrix_Object;
			};
			cbuffer PerMaterial : register(b2, space0);
			{
				int _IntValue;
				float _SpecularValue;
				float _MetallicValue;
				float _RoughnessValue;
				float4 _AlbedoColor;
			};	
			Texture2D _AlbedoMap : register(t0, space0); 
			SamplerState sampler_AlbedoMap : register(s0, space0);

			Varyings Vert(Attributes In, out float4 vertexCS : SV_POSITION)
			{
				Varyings Out = (Varyings)0;

				Out.uv0 = In.uv0;
				Out.normal = normalize(mul((float3x3)matrix_World, In.normalOS));
				Out.vertexWS = mul(matrix_World, float4(In.vertexOS.xyz, 1.0));
				vertexCS = mul(matrix_VP, Out.vertexWS);
				return Out;
			}
			
			void Frag(Varyings In, out float4 Diffuse : SV_Target0, out float4 Specular : SV_Target1)
			{
				float3 worldPos = In.vertexWS.xyz;
				float3 albedo = _AlbedoMap.Sample(sampler_AlbedoMap, In.uv).rgb;

				Diffuse = float4(albedo, 1);
				Specular = float4(albedo, 1);
			}
			ENDHLSL
		}

		Pass
		{
			Tags {"Name" = "IndexWrite" "Type" = "Compute"}

			HLSLPROGRAM
			#pragma compute Main

			#include "../Private/Common.hlsl"

			cbuffer PerDispatch : register(b0, space0);
			{
				float4 Resolution;
			};	
			RWTexture2D<float4> UAV_Output : register(u0, space0);

			[numthreads(8, 8, 1)]
			void Main(uint3 id : SV_DispatchThreadID)
			{
				UAV_Output[id.xy] = float4(id.x & id.y, (id.x & 15) / 15, (id.y & 15) / 15, 0);
			}
			ENDHLSL
		}

		Pass
		{
			Tags {"Name" = "RTAORayGen" "Type" = "RayTracing"}

			HLSLPROGRAM
			#pragma raygeneration RayGeneration

			#include "../Private/Common.hlsl"

			struct AORayPayload
			{
				float HitDistance;
			};

			struct AOAttributeData
			{
				// Barycentric value of the intersection
				float2 barycentrics;
			};

			cbuffer PerMaterial : register(b0, space0);
			{
				float _Specular;
				float4 _AlbedoColor;
			};
			cbuffer PerDispatch : register(b1, space0);
			{
				float4 Resolution;
			};
			RWTexture2D<float4> UAV_Output;

			[shader("raygeneration")]
			void RayGeneration()
			{
				uint2 dispatchIdx = DispatchRaysIndex().xy;
				uint2 launchDim   = DispatchRaysDimensions().xy;
				float2 uv = dispatchIdx * Resolution.zw;

				RayDesc rayDescriptor;
				rayDescriptor.TMin      = 0;
				rayDescriptor.TMax      = RTAO_Radius;
				rayDescriptor.Origin    = float3(0, 0, 0);
				rayDescriptor.Direction = float3(0, 0, 0);

        		AORayPayload rayPayLoad;
        		TraceRay(_RaytracingSceneStruct, RAY_FLAG_CULL_BACK_FACING_TRIANGLES, 0xFF, 0, 1, 0, rayDescriptor, rayPayLoad);

				UAV_Output[dispatchIdx] = RayIntersectionAO.HitDistance < 0 ? 1 : 0;
			}
			ENDHLSL
		}

		Pass
		{
			Tags {"Name" = "RTAOHitGroup" "Type" = "RayTracing"}

			HLSLPROGRAM
			#pragma miss Miss
			#pragma anyHit AnyHit
			#pragma closestHit ClosestHit

			#include "../Private/Common.hlsl"

			struct AORayPayload
			{
				float HitDistance;
			};

			struct AOAttributeData
			{
				// Barycentric value of the intersection
				float2 barycentrics;
			};

			cbuffer PerMaterial : register(b0, space0);
			{
				float _Specular;
				float4 _AlbedoColor;
			};	

			[shader("miss")]
			void Miss(inout AORayPayload rayPayload : SV_RayPayload)
			{
				rayPayload.HitDistance = -1;
			}

			[shader("anyhit")]
			void AnyHit(inout AORayPayload rayPayload : SV_RayPayload, AOAttributeData attributeData : SV_IntersectionAttributes)
			{
				IgnoreHit();
			}

			[shader("closesthit")]
			void ClosestHit(inout AORayPayload rayPayload : SV_RayPayload, AOAttributeData attributeData : SV_IntersectionAttributes)
			{
				rayPayload.HitDistance = RayTCurrent();
				//CalculateVertexData(FragInput);
			}
			ENDHLSL
		}
	}
}
```

```C#
public class MinExample
{
    static void Main(string[] args)
    {
        Shaderlab shaderLab = ShaderlabUtility.ParseShaderlabFromFile("D:\\Test\\UnlitShader.shader");
        Console.ReadKey();
    }
}
```

It will output 1 Catogory/Tags and 6 Pass/Tags and every string body inside HLSLPROGRAM:
