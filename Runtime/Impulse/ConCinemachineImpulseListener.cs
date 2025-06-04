using UnityEngine;

namespace Cinemachine
{
[SaveDuringPlay]
[ExecuteAlways]
public class ConCinemachineImpulseListener : CinemachineExtension
{
    [Tooltip("When to apply the impulse reaction.")]
    public CinemachineCore.Stage m_ApplyAfter = CinemachineCore.Stage.Noise;

    [Tooltip("Impulse events on channels not included in the mask will be ignored.")]
    [CinemachineImpulseChannelProperty]
    public int m_ChannelMask;

    [Tooltip("Gain to apply to the Impulse signal.")]
    public float m_Gain;

    [Tooltip("Enable this to perform distance calculation in 2D (ignore Z)")]
    public bool m_Use2DDistance;

    [Tooltip("Enable this to process all impulse signals in camera space")]
    public bool m_UseCameraSpace;

    [Tooltip("This controls the secondary reaction of the listener to the incoming impulse.")]
    public CinemachineImpulseListener.ImpulseReaction m_ReactionSettings;

    private void Reset()
    {
        m_ApplyAfter = CinemachineCore.Stage.Noise;
        m_ChannelMask = 1;
        m_Gain = 1;
        m_Use2DDistance = false;
        m_UseCameraSpace = true;
        m_ReactionSettings = new CinemachineImpulseListener.ImpulseReaction
        {
            m_AmplitudeGain = 1,
            m_FrequencyGain = 1,
            m_Duration = 1f
        };
    }

    protected override void PostPipelineStageCallback(
        CinemachineVirtualCameraBase vcam,
        CinemachineCore.Stage stage, ref CameraState state, float deltaTime
    )
    {
        if (stage == m_ApplyAfter && deltaTime >= 0)
        {
            bool haveImpulse = CinemachineImpulseManager.Instance.GetStrongestImpulseAt(
                state.FinalPosition, m_Use2DDistance, m_ChannelMask,
                out var impulsePos, out var impulseRot
            );
            bool haveReaction = m_ReactionSettings.GetReaction(
                deltaTime, impulsePos, out var reactionPos, out var reactionRot);

            if (haveImpulse)
            {
                impulseRot = Quaternion.SlerpUnclamped(Quaternion.identity, impulseRot, m_Gain);
                impulsePos *= m_Gain;
            }

            if (haveReaction)
            {
                impulsePos += reactionPos;
                impulseRot *= reactionRot;
            }

            if (haveImpulse || haveReaction)
            {
                if (m_UseCameraSpace)
                    impulsePos = state.RawOrientation * impulsePos;
                state.PositionCorrection += impulsePos;
                state.OrientationCorrection *= impulseRot;
            }
        }
    }
}
}