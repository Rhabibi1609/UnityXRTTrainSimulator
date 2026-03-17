using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;

public class scr_DisableWhenHolding : MonoBehaviour
{

    [SerializeField] private XRRayInteractor MainInteractor;
    [SerializeField] private XRRayInteractor ToggleInteractor;

    void Update()
    {
        if (MainInteractor.hasSelection)
        {
            ToggleInteractor.enabled = false;
        }
    }
}
