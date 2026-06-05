using Photon.Pun;
using UnityEngine;

namespace PeakDummyTools.DummyPlayers;

internal static class DummyControlMovementStateDriver
{
    internal static bool TryHandleSetMovementState(CharacterMovement movement)
    {
        if (movement == null
            || movement.character == null
            || !DummyControlSwitcher.IsControllingTarget(movement.character))
        {
            return false;
        }

        Character character = movement.character;
        CharacterInput input = character.input;
        CharacterData data = character.data;
        if (input == null || data == null)
        {
            return true;
        }

        UpdateCrouch(movement, character, input, data);
        UpdateSprint(movement, character, input, data);
        return true;
    }

    private static void UpdateCrouch(
        CharacterMovement movement,
        Character character,
        CharacterInput input,
        CharacterData data)
    {
        if (input.crouchToggleWasPressed)
        {
            movement.crouchToggleEnabled = !movement.crouchToggleEnabled;
        }

        if (!movement.crouchToggleEnabled)
        {
            SetCrouch(character, input.crouchIsPressed && data.isGrounded);
        }

        if (data.sinceGrounded <= 0.2f
            && !data.isSprinting
            && !data.isClimbing
            && !data.isRopeClimbing)
        {
            return;
        }

        SetCrouch(character, false);
        if (data.isGrounded && data.isSprinting)
        {
            movement.crouchToggleEnabled = false;
        }
    }

    private static void UpdateSprint(
        CharacterMovement movement,
        Character character,
        CharacterInput input,
        CharacterData data)
    {
        if (data.isGrounded)
        {
            if (input.sprintToggleWasPressed)
            {
                movement.sprintToggleEnabled = !movement.sprintToggleEnabled;
            }

            data.isSprinting = input.movementInput.y > 0.01f
                && (input.sprintIsPressed || movement.sprintToggleEnabled)
                && character.CheckSprint()
                && !character.OutOfRegularStamina();

            if (data.isSprinting)
            {
                character.UseStamina(movement.sprintStaminaUsage * Time.deltaTime, true);
            }
            else
            {
                movement.sprintToggleEnabled = false;
            }

            return;
        }

        data.isSprinting = input.movementInput.y > 0.01f
            && (input.sprintIsPressed || movement.sprintToggleEnabled)
            && character.CheckSprint();

        if (!data.isSprinting)
        {
            movement.sprintToggleEnabled = false;
        }
    }

    private static void SetCrouch(Character character, bool setCrouch)
    {
        if (character.data.isCrouching == setCrouch || character.refs?.view == null)
        {
            return;
        }

        character.refs.view.RPC(
            nameof(CharacterMovement.RPCA_SetCrouch),
            RpcTarget.All,
            setCrouch);
    }
}
