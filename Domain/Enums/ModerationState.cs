namespace UsluzionicaServer.Domain.Enums;

/// <summary>
/// Moderacijsko stanje oglasa. Beleži ISKLJUČIVO odluke admina.
///
/// Broj prijava se namerno ne čuva ovde nego se računa agregatom nad
/// <c>Reports</c>. Denormalizovan brojač bi bio drugi izvor istine o istom
/// podatku, a dva izvora se pre ili kasnije raziđu — i to tiho.
///
/// Zato ovde stoje samo vrednosti koje ima ko da postavi eksplicitno: admin,
/// jednim klikom, jednom.
/// </summary>
public enum ModerationState
{
    /// <summary>Podrazumevano. Nikad nije bilo odluke o ovom oglasu.</summary>
    Clean,

    /// <summary>
    /// Admin je pregledao prijave i utvrdio da je oglas u redu.
    ///
    /// Postoji da bi se ponovljeni talas prijava na isti oglas prepoznao kao
    /// maltretiranje, a ne kao nov slučaj.
    /// </summary>
    Cleared,

    /// <summary>Admin je prihvatio prijavu i oglas je uklonjen.</summary>
    Removed
}
