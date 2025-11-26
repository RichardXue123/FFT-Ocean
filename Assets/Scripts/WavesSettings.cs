using UnityEngine;

public struct SpectrumSettings
{
    public float scale;
    public float angle;
    public float spreadBlend;
    public float swell;
    public float alpha;
    public float peakOmega;
    public float gamma;
    public float shortWavesFade;
}

[System.Serializable]
public struct DisplaySpectrumSettings
{
    [Range(0, 1)]
    public float scale;
    public Vector2 windSpeed;
    //public float windSpeed;
    //public float windDirection;
    public float fetch;
    [Range(0, 1)]
    public float spreadBlend;
    [Range(0, 1)]
    public float swell;
    public float peakEnhancement;
    public float shortWavesFade;
}

[CreateAssetMenu(fileName = "New waves settings", menuName = "Ocean/Waves Settings")]
public class WavesSettings : ScriptableObject
{
    public float g;
    public float depth;
    [Range(0, 1)]
    public float lambda;
    public DisplaySpectrumSettings local;
    public DisplaySpectrumSettings swell;

    public SpectrumSettings[] spectrums = new SpectrumSettings[2];

    public void SetParametersToShader(ComputeShader shader, int kernelIndex, ComputeBuffer paramsBuffer)
    {
        shader.SetFloat(G_PROP, g);
        shader.SetFloat(DEPTH_PROP, depth);

        FillSettingsStruct(local, ref spectrums[0]);
        FillSettingsStruct(swell, ref spectrums[1]);

        paramsBuffer.SetData(spectrums);
        shader.SetBuffer(kernelIndex, SPECTRUMS_PROP, paramsBuffer);
    }

    void FillSettingsStruct(DisplaySpectrumSettings display, ref SpectrumSettings settings)
    {
        settings.scale = display.scale;
        //settings.angle = display.windDirection / 180 * Mathf.PI;
        settings.angle = Mathf.Atan2(-display.windSpeed.y, -display.windSpeed.x);
        settings.spreadBlend = display.spreadBlend;
        settings.swell = Mathf.Clamp(display.swell, 0.01f, 1);
        //settings.alpha = JonswapAlpha(g, display.fetch, display.windSpeed);
        //settings.peakOmega = JonswapPeakFrequency(g, display.fetch, display.windSpeed);
        float windSpeedMag = display.windSpeed.magnitude;
        settings.alpha = JonswapAlpha(g, display.fetch, windSpeedMag);
        settings.peakOmega = JonswapPeakFrequency(g, display.fetch, windSpeedMag);
        settings.gamma = display.peakEnhancement;
        settings.shortWavesFade = display.shortWavesFade;
    }

    float JonswapAlpha(float g, float fetch, float windSpeed)
    {
        return 0.076f * Mathf.Pow(g * fetch / windSpeed / windSpeed, -0.22f);
    }

    float JonswapPeakFrequency(float g, float fetch, float windSpeed)
    {
        return 22 * Mathf.Pow(windSpeed * fetch / g / g, -0.33f);
        //return 22.84f * Mathf.Pow(g,0.7f) * Mathf.Pow(fetch,-0.3f) * Mathf.Pow(windSpeed,-0.4f);
    }

    readonly int G_PROP = Shader.PropertyToID("GravityAcceleration");
    readonly int DEPTH_PROP = Shader.PropertyToID("Depth");
    readonly int SPECTRUMS_PROP = Shader.PropertyToID("Spectrums");
}
