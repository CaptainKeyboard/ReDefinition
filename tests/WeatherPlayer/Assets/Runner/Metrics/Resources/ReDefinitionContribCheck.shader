// The test pass of the contribution mask (tests/WeatherPlayer, scene contrib-check): a flat colour blended like an atmosphere pass,
// returned through AtmoContribOutput of unity/Assets/ReDefinitionAtmospheres/Include/AtmoContribution.cginc. The include is carried in
// the player's Resources by tests/WeatherPlayer/linked/B11.txt.
Shader "Hidden/ReDefinition/ContribCheck"
{
    Properties
    {
        _Colour ("Colour", Color) = (0.5, 0.5, 0.5, 1)
        _Contribution ("Contribution", Float) = 1
        _FeatureId ("Feature id", Float) = 15
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Assets/Linked/Resources/AtmoContribution.cginc"

            float4 _Colour;
            float _Contribution;
            float _FeatureId;

            float4 vert(float4 vertex : POSITION) : SV_POSITION
            {
                return UnityObjectToClipPos(vertex);
            }

            float4 frag() : SV_Target
            {
                return AtmoContribOutput(_Colour, _Contribution, (int)(_FeatureId + 0.5));
            }
            ENDCG
        }
    }
}
