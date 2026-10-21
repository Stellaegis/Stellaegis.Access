# Stellaegis.Access

The decision point of an application assembled on
[Stellaegis.Primitives](https://github.com/Stellaegis/Stellaegis.Primitives): an actor, a space, an operation — yes
or no. It holds the effective rights that a registry projects into it, and answers the policy per operation that
`Stellaegis.Primitives.AspNetCore` attaches to a module's routes. It knows neither accounts, nor tokens, nor roles.

⚠ Being extracted from the Stellaegis framework. Nothing is published yet; the first version will be 5.0.0.
