using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;


public class scr_DisableWhenHolding : MonoBehaviour
{

    [SerializeField] private UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor MainInteractor;
    [SerializeField] private UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor ToggleInteractor;

    void Update()
    {
        if (MainInteractor.hasSelection)
        {
            ToggleInteractor.enabled = false;
        }
    }
}
