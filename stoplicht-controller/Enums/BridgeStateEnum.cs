/// <summary>
/// Status van de brugcyclus
/// </summary>
enum BridgeState
{
    Closed,         // Normaal wegverkeer, brug dicht
    Opening,        // Barrières sluiten, brug opent
    BoatsA,         // Boten van richting A doorlaten
    BoatsB,         // Boten van richting B doorlaten
    Closing,        // Brug sluit
    EmergencyClose, // Noodprocedure: brug zo snel mogelijk sluiten
    PriorityRoute   // Voorrangsroute actief
}